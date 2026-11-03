using Dapper;
using Governance.Application.Outbox;
using Governance.Seed;
using Microsoft.Data.SqlClient;

namespace Governance.IntegrationTests;

/// <summary>Schema behaviors that hold whatever the application does: idempotent deploy, one A, ledger, temporal, outbox.</summary>
[Collection("sql")]
public sealed class SchemaTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public void Deploy_runs_twice_without_change()
    {
        using var c = db.Admin();
        var before = c.QuerySingle<(int Cells, int Stages, int Transitions, int Rules)>(
            "SELECT (SELECT COUNT(*) FROM sec.RaciCell), (SELECT COUNT(*) FROM wf.StageDefinition), (SELECT COUNT(*) FROM wf.TransitionDefinition), (SELECT COUNT(*) FROM ntf.NotificationRule)");
        db.Deploy(createLogins: false);
        var after = c.QuerySingle<(int Cells, int Stages, int Transitions, int Rules)>(
            "SELECT (SELECT COUNT(*) FROM sec.RaciCell), (SELECT COUNT(*) FROM wf.StageDefinition), (SELECT COUNT(*) FROM wf.TransitionDefinition), (SELECT COUNT(*) FROM ntf.NotificationRule)");
        Assert.Equal(before, after);
        Assert.Equal(19 * 9, after.Cells);
        Assert.Equal(22, after.Rules);
    }

    /// <summary>
    /// An upgrade runs over a live database: it must not end the site's and worker's transactions (READ_COMMITTED_SNAPSHOT
    /// is set once, not on every run), and row-level security must be on when it is done.
    /// </summary>
    [Fact]
    public void A_rerun_keeps_open_transactions_and_row_level_security()
    {
        using var live = db.Admin();
        live.Execute("CREATE TABLE #busy (i int);");
        using (var tran = live.BeginTransaction())
        {
            live.Execute("INSERT #busy VALUES (1);", transaction: tran);
            db.Deploy(createLogins: false);
            Assert.Equal(1, live.ExecuteScalar<int>("SELECT COUNT(*) FROM #busy", transaction: tran));
            tran.Commit();   // still this session's transaction: the deploy did not roll it back
        }
        using var c = db.Admin();
        Assert.True(c.ExecuteScalar<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.security_policies WHERE name = 'FundingOwnerPolicy' AND is_enabled = 1"));
    }

    /// <summary>The policy is rebuilt in one transaction: a script that fails halfway leaves the previous policy on.</summary>
    [Fact]
    public void A_row_level_security_script_that_fails_halfway_leaves_the_policy_on()
    {
        var script = File.ReadAllText(Path.Combine(db.RepoRoot, "src", "Governance.Database", "Scripts", "100-rls.sql"));
        var at = script.IndexOf("CREATE SECURITY POLICY", StringComparison.Ordinal);
        Assert.True(at > 0);
        // Fails after the old policy and its functions were dropped and the new functions created, before the new policy.
        var broken = script[..at] + "THROW 50001, 'Simulated failure before the policy is recreated.', 1;\nGO\n" + script[at..];
        var file = Path.Combine(Path.GetTempPath(), $"cgw-rls-{Guid.NewGuid():n}.sql");
        File.WriteAllText(file, broken);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sqlcmd") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-S", db.Server, "-E", "-C", "-d", db.Database, "-b", "-I", "-i", file }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(TimeSpan.FromMinutes(2)));
            Assert.NotEqual(0, p.ExitCode);
            Assert.Contains("Simulated failure", output);
        }
        finally { File.Delete(file); }
        using var c = db.Admin();
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.security_policies WHERE name = 'FundingOwnerPolicy' AND is_enabled = 1"));
        Assert.True(c.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id WHERE s.name = 'FundingOwnerPolicy'") >= 28);
    }

    /// <summary>
    /// The daily ledger digest runs as the worker's account (Save-LedgerDigest.ps1): it can generate one, and where the
    /// engine has a GENERATE LEDGER DIGEST permission app_worker holds it.
    /// </summary>
    [Fact]
    public void The_worker_login_can_generate_a_ledger_digest()
    {
        using var w = new SqlConnection(db.WorkerConnectionString);
        w.Open();
        var digest = w.ExecuteScalar<string>("EXEC sys.sp_generate_database_ledger_digest;");
        Assert.Contains("\"block_id\"", digest);
        using var c = db.Admin();
        if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.fn_builtin_permissions('DATABASE') WHERE permission_name = N'GENERATE LEDGER DIGEST'") == 1)
            Assert.Equal(1, c.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'app_worker') AND permission_name = N'GENERATE LEDGER DIGEST' AND state = 'G'"));
        // The digest job's row on Admin > Integrations: the worker may write it.
        w.Execute("INSERT intg.JobRun (JobName, Node, StartedUtc, FinishedUtc, Status, Message) VALUES ('LedgerDigest', N'test', SYSUTCDATETIME(), SYSUTCDATETIME(), 'SUCCEEDED', N'test')");
    }

    [Fact]
    public void Raci_seed_has_exactly_one_accountable_per_activity_and_the_od13_sourcing_values()
    {
        using var c = db.Admin();
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM (SELECT ActivityCode FROM sec.RaciCell WHERE RaciValue IN ('A','A/R') GROUP BY RaciVersionId, ActivityCode HAVING COUNT(*) <> 1) x"));
        Assert.Equal(19, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sec.RaciCell WHERE RaciValue IN ('A','A/R')"));
        var sourcing = c.Query<(string, string)>("SELECT ActivityCode, RaciValue FROM sec.RaciCell WHERE FunctionCode = 'SOURCING'").ToDictionary(x => x.Item1, x => x.Item2);
        Assert.Equal("C", sourcing["LIFECYCLE_RETIREMENT"]);
        foreach (var a in new[] { "ENV_POLICY_PATTERN", "TECH_IMPLEMENTATION", "MONITORING_RESPONSE", "MS_EVIDENCE", "SECURITY_RISK" }) Assert.Equal("I", sourcing[a]);
    }

    [Fact]
    public void A_second_accountable_is_rejected_by_the_unique_index()
    {
        using var c = db.Admin();
        using var tx = c.BeginTransaction();
        var ex = Assert.Throws<SqlException>(() => c.Execute(
            "UPDATE sec.RaciCell SET RaciValue = 'A' WHERE ActivityCode = 'BUSINESS_CASE' AND FunctionCode = 'PLATFORM_OWNER'", transaction: tx));
        Assert.Contains(ex.Number, new[] { 2601, 2627 });
        tx.Rollback();
    }

    [Theory]
    [InlineData("wf.Decision", "UPDATE wf.Decision SET Outcome = 'DECLINED'")]
    [InlineData("wf.Decision", "DELETE FROM wf.Decision")]
    [InlineData("aud.AuditLog", "UPDATE aud.AuditLog SET Action = 'tampered'")]
    [InlineData("aud.AuditLog", "DELETE FROM aud.AuditLog")]
    public void Ledger_tables_are_append_only(string table, string tamper)
    {
        using var c = db.Admin();
        if (table == "wf.Decision")
            c.Execute("""
                INSERT wf.Decision (PublicId, DecisionType, Outcome, DeciderPersonId, DeciderRoleCode, RaciVersionId, RaciActivityCode, ScopeText, Source, DecidedAtUtc)
                VALUES (NEWID(), 'ROUTINE_APPROVAL', 'APPROVED', 1, 'LOB_BUSINESS_OWNER', 1, 'FUNDING_BUDGET', N'test', 'WORKBENCH', SYSUTCDATETIME())
                """);
        else
            c.Execute("INSERT aud.AuditLog (Utc, Action, Outcome) VALUES (SYSUTCDATETIME(), 'test', 'OK')");
        Assert.Throws<SqlException>(() => c.Execute(tamper));
    }

    [Fact]
    public void Temporal_history_answers_who_owned_it_then()
    {
        using var c = db.Admin();
        var a = c.ExecuteScalar<int>("INSERT sec.Person (DisplayName, IsSynthetic) OUTPUT inserted.PersonId VALUES (N'Owner A', 1)");
        var b = c.ExecuteScalar<int>("INSERT sec.Person (DisplayName, IsSynthetic) OUTPUT inserted.PersonId VALUES (N'Owner B', 1)");
        var fo = c.ExecuteScalar<int>("INSERT core.FundingOwner (Code, Name, OwnerType, BusinessOwnerPersonId, Status, EffectiveFrom) OUTPUT inserted.FundingOwnerId VALUES ('TEMPORAL', N'Temporal test', 'LOB', @a, 'DRAFT', '2026-01-01')", new { a });
        var then = c.ExecuteScalar<DateTime>("WAITFOR DELAY '00:00:00.050'; SELECT SYSUTCDATETIME();");
        c.Execute("WAITFOR DELAY '00:00:00.050'; UPDATE core.FundingOwner SET BusinessOwnerPersonId = @b WHERE FundingOwnerId = @fo", new { b, fo });
        Assert.Equal(b, c.ExecuteScalar<int>("SELECT BusinessOwnerPersonId FROM core.FundingOwner WHERE FundingOwnerId = @fo", new { fo }));
        Assert.Equal(a, c.ExecuteScalar<int>("SELECT BusinessOwnerPersonId FROM core.FundingOwner FOR SYSTEM_TIME AS OF @then WHERE FundingOwnerId = @fo", new { then, fo }));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM core.FundingOwnerHistory WHERE FundingOwnerId = @fo", new { fo }));
    }

    [Fact]
    public async Task Outbox_ignores_a_repeat_with_the_same_dedupe_key()
    {
        var host = new EngineHost(db.AdminConnectionString);
        var writer = new OutboxWriter();
        var key = "TEST:" + Guid.NewGuid();
        var first = await host.SystemAsync(u => writer.EnqueueAsync(u, new OutboxMessage { EventType = "N01", Subject = "x" }, key));
        var second = await host.SystemAsync(u => writer.EnqueueAsync(u, new OutboxMessage { EventType = "N01", Subject = "y" }, key));
        Assert.True(first);
        Assert.False(second);
        using var c = db.Admin();
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM ntf.Outbox WHERE DedupeKey = @key", new { key }));
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_outbox_row()
    {
        using (var c = db.Admin())
            c.Execute("""
                INSERT ntf.Outbox (EventType, Payload, DedupeKey)
                SELECT TOP (80) 'N17', N'{}', CONCAT('CLAIM:', NEWID()) FROM sys.all_objects
                """);
        const string claim = """
            UPDATE TOP (50) o
            SET    LockedBy = @node, LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME()), Attempts = Attempts + 1
            OUTPUT inserted.OutboxId
            FROM   ntf.Outbox AS o WITH (ROWLOCK, READPAST, UPDLOCK)
            WHERE  o.ProcessedAtUtc IS NULL AND o.AvailableAtUtc <= SYSUTCDATETIME()
              AND  (o.LockedUntilUtc IS NULL OR o.LockedUntilUtc < SYSUTCDATETIME()) AND o.DedupeKey LIKE 'CLAIM:%';
            """;
        await using var a = new SqlConnection(db.WorkerConnectionString);
        await using var b = new SqlConnection(db.WorkerConnectionString);
        await a.OpenAsync(); await b.OpenAsync();
        await using var ta = await a.BeginTransactionAsync();
        await using var tb = await b.BeginTransactionAsync();
        var claimedA = (await a.QueryAsync<long>(claim, new { node = "A" }, ta)).ToList();
        var claimedB = (await b.QueryAsync<long>(claim, new { node = "B" }, tb)).ToList();   // A still holds its row locks
        await ta.CommitAsync(); await tb.CommitAsync();
        Assert.Equal(50, claimedA.Count);
        Assert.Equal(30, claimedB.Count);
        Assert.Empty(claimedA.Intersect(claimedB));
    }

    [Fact]
    public void The_web_login_is_not_db_owner_and_cannot_change_the_schema()
    {
        using var c = new SqlConnection(db.WebConnectionString);
        c.Open();
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT IS_MEMBER('db_owner')"));
        Assert.Equal(1, c.ExecuteScalar<int>("SELECT IS_MEMBER('app_web')"));
        Assert.Throws<SqlException>(() => c.Execute("CREATE TABLE dbo.Nope (Id int)"));
        Assert.Throws<SqlException>(() => c.Execute("DELETE FROM sec.RaciCell"));
    }
}
