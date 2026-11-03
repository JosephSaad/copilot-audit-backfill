using System.Text;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Application.Integrations;

/// <summary>
/// X7: the Agent Registry export (CSV from the Microsoft 365 admin center), uploaded by an operator. Rows are matched to
/// registry or Copilot Studio agent bindings; unmatched agents raise UNMATCHED_REGISTRY_AGENT findings.
/// </summary>
public sealed class RegistryImportService(Db db, FindingWriter findings) : IRegistryImport
{
    public Task<Guid> ImportAsync(Stream csv, string fileName, int operatorPersonId, CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Registry, "UPLOAD", operatorPersonId, async (runId, uow) =>
        {
            var rows = ParseCsv(csv);
            if (rows.Count == 0) throw new DomainException("The file has no rows. Export the Agent Registry as CSV from the Microsoft 365 admin center and upload it as is.");
            var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
            var cId = Col("agent id", "agentid", "id", "app id", "appid");
            var cName = Col("name", "agent name", "display name", "displayname");
            var cPlatform = Col("platform", "source", "type", "agent type");
            var cOwner = Col("owner", "owners", "owner upn", "created by", "publisher");
            var cStatus = Col("status", "state", "availability");
            var cAudience = Col("audience", "available to", "users", "deployment");
            if (cId < 0) throw new DomainException("The file has no agent ID column (expected a column named Agent ID or ID).");

            int read = 0, raised = 0;
            var open = new List<string>();
            foreach (var r in rows.Skip(1))
            {
                string? V(int i) => i >= 0 && i < r.Count && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
                var agentId = V(cId);
                if (agentId is null) continue;
                read++;
                var matched = await uow.ScalarAsync<int>(
                    "SELECT COUNT(*) FROM core.WorkloadBinding WHERE ObjectType IN ('REGISTRY_AGENT','CS_AGENT') AND ExternalId = @agentId AND EffectiveTo IS NULL", new { agentId }) > 0;
                await uow.ExecuteAsync(
                    "INSERT intg.RegistryRow (SyncRunId, AgentId, Name, Platform, OwnerUpn, Status, Audience, Matched) VALUES (@runId, @agentId, @name, @platform, @owner, @status, @audience, @matched)",
                    new { runId, agentId = Cut(agentId, 200), name = Cut(V(cName), 400), platform = Cut(V(cPlatform), 100), owner = Cut(V(cOwner), 256), status = Cut(V(cStatus), 60), audience = Cut(V(cAudience), 400), matched });
                if (matched) continue;
                var key = $"UNMATCHED_REGISTRY:{agentId.ToLowerInvariant()}";
                open.Add(key);
                if (await findings.RaiseAsync(uow, key, new FindingWriter.NewFinding(FindingTypes.UnmatchedRegistryAgent, "MEDIUM",
                        $"Agent in the registry but not in the Workbench: {V(cName) ?? agentId}",
                        $"Platform {V(cPlatform) ?? "unknown"}, owner {V(cOwner) ?? "unknown"}. Register it or record why it is out of scope.",
                        ExternalId: agentId, SyncRunId: runId)) is not null) raised++;
            }
            await findings.ResolveMissingAsync(uow, FindingTypes.UnmatchedRegistryAgent, open, "Registered or no longer in the registry export.");
            return (read, raised, $"{fileName}: {read} agents, {raised} new unmatched.");
        }, ct);

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, commas and line breaks inside quotes.</summary>
    public static List<List<string>> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n': row.Add(field.ToString()); field.Clear(); if (row.Any(f => f.Length > 0)) rows.Add(row); row = new List<string>(); break;
                default: field.Append(ch); break;
            }
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row);
        return rows;
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>X3 (daily during campaigns): Entra access review decisions as user-level recertification evidence (I1).</summary>
public sealed class AccessReviewImportService(Db db, IAccessReviewApi reviews)
{
    public Task<Guid> RunAsync(CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.AccessReviews, "APP_ONLY", null, async runId =>
        {
            var campaigns = (await db.InTransactionAsync(uow => uow.QueryAsync<(int CampaignId, string DefinitionId)>(
                "SELECT CampaignId, AccessReviewDefinitionId FROM wf.Campaign WHERE Status = 'OPEN' AND CampaignType = 'RECERTIFICATION' AND AccessReviewDefinitionId IS NOT NULL"), ct)).ToList();
            var read = 0;
            foreach (var c in campaigns)
                foreach (var instance in await reviews.InstanceIdsAsync(c.DefinitionId, ct))
                {
                    var decisions = await reviews.DecisionsAsync(c.DefinitionId, instance, ct);
                    read += decisions.Count;
                    await db.InTransactionAsync(async uow =>
                    {
                    foreach (var d in decisions)
                        await uow.ExecuteAsync(
                            """
                            MERGE intg.AccessReviewDecision AS t USING (SELECT @key AS DecisionKey) AS s ON t.DecisionKey = s.DecisionKey
                            WHEN MATCHED THEN UPDATE SET Decision = @Decision, ReviewedByObjectId = @ReviewedById, ReviewedUtc = @ReviewedUtc, Justification = @Justification, SyncRunId = @runId
                            WHEN NOT MATCHED THEN INSERT (DecisionKey, ReviewDefinitionId, InstanceId, PrincipalObjectId, Decision, ReviewedByObjectId, ReviewedUtc, Justification, CampaignId, SyncRunId)
                                 VALUES (@key, @DefinitionId, @instance, @PrincipalId, @Decision, @ReviewedById, @ReviewedUtc, @Justification, @CampaignId, @runId);
                            """,
                            new
                            {
                                key = $"{c.DefinitionId}:{instance}:{d.Id}", c.DefinitionId, instance, d.PrincipalId, d.Decision, d.ReviewedById, d.ReviewedUtc,
                                Justification = d.Justification is { Length: > 1000 } j ? j[..1000] : d.Justification, c.CampaignId, runId,
                            });
                    }, ct);
                }
            return (read, 0, $"{campaigns.Count} open campaigns with an access review, {read} decisions.");
        }, ct);
}

/// <summary>Health of each integration, from the last sync runs and job runs (job dashboard, freshness badges).</summary>
public sealed class IntegrationStatusService(Db db, IClock clock, TenantSettings tenant) : IIntegrationStatus
{
    public async Task<IReadOnlyList<IntegrationHealth>> GetAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenSystemAsync(ct);
        var runs = (await Dapper.SqlMapper.QueryAsync<(string Integration, string Mode, DateTime? LastSuccess, DateTime? LastRun, string? LastStatus, string? Message)>(conn,
            """
            SELECT r.Integration, r.Mode,
                   (SELECT MAX(FinishedUtc) FROM intg.SyncRun s WHERE s.Integration = r.Integration AND s.Status = 'SUCCEEDED') AS LastSuccess,
                   r.StartedUtc AS LastRun, r.Status AS LastStatus, r.Message
            FROM intg.SyncRun r
            WHERE r.SyncRunId = (SELECT MAX(SyncRunId) FROM intg.SyncRun x WHERE x.Integration = r.Integration)
            """)).ToDictionary(r => r.Integration);
        var deliveries = (await Dapper.SqlMapper.QueryAsync<(string Channel, DateTime? LastOk, DateTime? LastAny, string? LastStatus)>(conn,
            """
            SELECT d.Channel, (SELECT MAX(Utc) FROM ntf.Delivery x WHERE x.Channel = d.Channel AND x.Status IN ('SENT','RECORDED')) AS LastOk,
                   d.Utc AS LastAny, d.Status AS LastStatus
            FROM ntf.Delivery d WHERE d.DeliveryId = (SELECT MAX(DeliveryId) FROM ntf.Delivery y WHERE y.Channel = d.Channel)
            """)).ToDictionary(d => d.Channel);
        var itsm = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<(DateTime? LastOk, DateTime? LastRun, string? Status)>(conn,
            "SELECT TOP (1) (SELECT MAX(FinishedUtc) FROM intg.JobRun WHERE JobName = 'ItsmPoller' AND Status = 'SUCCEEDED'), StartedUtc, Status FROM intg.JobRun WHERE JobName = 'ItsmPoller' ORDER BY JobRunId DESC");

        var now = clock.UtcNow;
        var list = new List<IntegrationHealth>();
        foreach (var (code, name, fresh) in IntegrationCodes.All)
        {
            switch (code)
            {
                case IntegrationCodes.Email or IntegrationCodes.TeamsActivity or IntegrationCodes.TeamsChannel:
                {
                    var ch = code == IntegrationCodes.Email ? "EMAIL" : code == IntegrationCodes.TeamsActivity ? "ACTIVITY" : "CHANNEL";
                    deliveries.TryGetValue(ch, out var d);
                    list.Add(new IntegrationHealth(code, name, ch == "EMAIL" ? $"Graph sendMail from {tenant.Mailbox ?? "(not set)"}" : ch == "ACTIVITY" ? "Graph activity feed" : "Power Automate flow",
                        d.LastOk, d.LastAny, d.LastStatus, d.LastStatus is "FAILED", d.LastAny is null ? "No deliveries yet." : null));
                    break;
                }
                case IntegrationCodes.Itsm:
                    list.Add(new IntegrationHealth(code, name, "REST", itsm.LastOk, itsm.LastRun, itsm.Status, itsm.LastOk is null || now - itsm.LastOk > fresh, null));
                    break;
                default:
                    runs.TryGetValue(code, out var r);
                    var stale = r.LastSuccess is null ? r.LastRun is not null : now - r.LastSuccess > fresh;
                    var note = r.LastRun is null
                        ? code switch
                        {
                            IntegrationCodes.Inventory => "Never run. An operator runs it from Operations > Sync (delegated sign-in).",
                            IntegrationCodes.Registry => "No export uploaded yet.",
                            IntegrationCodes.M365Attestation => "Attestation, not a sync: see Operations > Attestations.",
                            IntegrationCodes.AgentKit => "Optional; not in use (OD10).",
                            _ => "Not run yet.",
                        }
                        : r.Message;
                    list.Add(new IntegrationHealth(code, name, r.Mode ?? "", r.LastSuccess, r.LastRun, r.LastStatus, stale, note));
                    break;
            }
        }
        return list;
    }
}

/// <summary>Turns an outbox message's targets into people (section 8.1 recipient resolver).</summary>
public sealed class RecipientResolver(AssignmentResolver assignments)
{
    public sealed record Recipient(int PersonId, string DisplayName, string? Address, Guid? EntraObjectId, bool ActivityFeed, TimeSpan? QuietStart, TimeSpan? QuietEnd);

    public async Task<IReadOnlyList<Recipient>> ResolveAsync(Uow uow, OutboxMessage m)
    {
        var ids = new HashSet<int>(m.PersonIds.Where(p => p > 0));
        foreach (var t in m.Roles)
        {
            var role = await assignments.EffectiveRoleAsync(uow, t.RoleCode, t.ScopeId);
            var holders = await assignments.HoldersAsync(uow, role, t.ScopeType, t.ScopeType == ScopeTypes.FundingOwner ? t.ScopeId : null,
                includeDelegates: Roles.BusinessOwnerRoles.Contains(role) || t.RoleCode == Roles.DelegatedApprover);
            foreach (var h in holders) ids.Add(h);
            if (t.RoleCode == Roles.DelegatedApprover && t.ScopeId is int fo)
                foreach (var d in await uow.QueryAsync<int>(
                             "SELECT DelegatePersonId FROM sec.Delegation WHERE FundingOwnerId = @fo AND Status = 'ACTIVE' AND EffectiveFrom <= CAST(SYSUTCDATETIME() AS date) AND (EffectiveTo IS NULL OR EffectiveTo >= CAST(SYSUTCDATETIME() AS date))", new { fo }))
                    ids.Add(d);
        }
        // People excluded from the task by segregation of duties are not asked to act on it.
        if (m.TaskId is long taskId)
            foreach (var x in await uow.QueryAsync<int>("SELECT PersonId FROM wf.TaskExclusion WHERE TaskId = @taskId", new { taskId }))
                if (!m.PersonIds.Contains(x)) ids.Remove(x);
        if (ids.Count == 0) return Array.Empty<Recipient>();
        return (await uow.QueryAsync<Recipient>(
            """
            SELECT p.PersonId, p.DisplayName, COALESCE(p.Mail, CASE WHEN p.UserPrincipalName LIKE '%@%' THEN p.UserPrincipalName END) AS Address, p.EntraObjectId,
                   COALESCE(pr.ActivityFeed, CAST(1 AS bit)) AS ActivityFeed, CAST(pr.QuietStart AS time) AS QuietStart, CAST(pr.QuietEnd AS time) AS QuietEnd
            FROM sec.Person p LEFT JOIN ntf.Preference pr ON pr.PersonId = p.PersonId
            WHERE p.PersonId IN (SELECT value FROM OPENJSON(@ids)) AND p.AccountEnabled = 1
            """, new { ids = System.Text.Json.JsonSerializer.Serialize(ids) })).ToList();
    }
}
