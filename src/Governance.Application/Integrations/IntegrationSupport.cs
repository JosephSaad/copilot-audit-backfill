using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Domain;

namespace Governance.Application.Integrations;

/// <summary>
/// Tenant-specific IDs written by setup\Setup-TestTenant.ps1 (config\tenant.test.json). IDs only, no secrets.
/// </summary>
public sealed class TenantSettings
{
    public string? TenantId { get; set; }
    public string? TenantDomain { get; set; }
    public string? WebClientId { get; set; }
    public string? WorkerClientId { get; set; }
    public string? WorkerCertificateSubject { get; set; }
    public string? WorkerCertificateThumbprint { get; set; }
    public string? Mailbox { get; set; }
    public string? TeamsAppId { get; set; }
    public string? CatalogId { get; set; }
    public string? WebBaseUrl { get; set; }
    public bool PowerPlatformReader { get; set; }
    public Dictionary<string, string> CentralGroups { get; set; } = new();
    public List<PolicyGroupSetting> PolicyGroups { get; set; } = new();
}

public sealed class PolicyGroupSetting
{
    public string? Lob { get; set; }
    public string? Service { get; set; }
    public string? Tier { get; set; }
    public string? GroupId { get; set; }
    public string? Name { get; set; }
    public string? AccessPackageId { get; set; }
}

/// <summary>Integration codes (section 6.6) with their freshness targets (section 12).</summary>
public static class IntegrationCodes
{
    public const string Directory = "X1";
    public const string Entitlements = "X2";
    public const string AccessReviews = "X3";
    public const string Inventory = "X4";
    public const string Licensing = "X5";
    public const string M365Attestation = "X6";
    public const string Registry = "X7";
    public const string Itsm = "X8";
    public const string Email = "X9";
    public const string TeamsActivity = "X10";
    public const string TeamsChannel = "X11";
    public const string AgentKit = "X12";

    public static readonly IReadOnlyList<(string Code, string Name, TimeSpan Fresh)> All = new[]
    {
        (Directory, "Entra directory", TimeSpan.FromMinutes(30)),
        (Entitlements, "Entra access packages", TimeSpan.FromMinutes(30)),
        (AccessReviews, "Entra access reviews", TimeSpan.FromDays(2)),
        (Inventory, "Power Platform inventory (operator-run)", TimeSpan.FromDays(7)),
        (Licensing, "Power Platform licensing", TimeSpan.FromHours(26)),
        (M365Attestation, "Microsoft 365 settings (attestation)", TimeSpan.FromDays(35)),
        (Registry, "Agent Registry export", TimeSpan.FromDays(35)),
        (Itsm, "ITSM", TimeSpan.FromMinutes(15)),
        (Email, "Email (notification mailbox)", TimeSpan.FromDays(1)),
        (TeamsActivity, "Teams activity feed", TimeSpan.FromDays(7)),
        (TeamsChannel, "Teams channels (Power Automate)", TimeSpan.FromDays(7)),
        (AgentKit, "Copilot Agent Kit (optional)", TimeSpan.FromDays(2)),
    };
}

/// <summary>Records intg.SyncRun rows: one per integration run, with counts and a short message (never raw error text for users).</summary>
public static class SyncRuns
{
    public static Task<(long Id, Guid PublicId)> StartAsync(Uow uow, string integration, string mode, int? personId)
        => uow.QuerySingleAsync<(long, Guid)>(
            "INSERT intg.SyncRun (Integration, Mode, TriggeredByPersonId, Status) OUTPUT inserted.SyncRunId, inserted.PublicId VALUES (@integration, @mode, @personId, 'RUNNING')",
            new { integration, mode, personId });

    public static Task FinishAsync(Uow uow, long runId, string status, int items, int findings, string? message)
        => uow.ExecuteAsync(
            "UPDATE intg.SyncRun SET FinishedUtc = SYSUTCDATETIME(), Status = @status, ItemsRead = @items, FindingsRaised = @findings, Message = @message WHERE SyncRunId = @runId",
            new { runId, status, items, findings, message = message is { Length: > 1000 } ? message[..1000] : message });

    /// <summary>
    /// Starts and finishes a run in its own short transactions, so a failure is still recorded. The work runs in ONE
    /// transaction: use this only for work that makes no external calls (an uploaded file). Work that calls Microsoft
    /// or ITSM uses the overload without a Uow and opens short transactions itself between calls.
    /// </summary>
    public static Task<Guid> RunAsync(Db db, string integration, string mode, int? personId, Func<long, Uow, Task<(int Items, int Findings, string? Message)>> work, CancellationToken ct)
        => RunAsync(db, integration, mode, personId, runId => db.InTransactionAsync(u => work(runId, u), ct), ct);

    /// <summary>
    /// Starts and finishes a run; the work fetches from the outside world with no transaction open and writes each page
    /// or batch in its own short transaction (db.InTransactionAsync), so locks are never held across HTTP calls.
    /// </summary>
    public static async Task<Guid> RunAsync(Db db, string integration, string mode, int? personId, Func<long, Task<(int Items, int Findings, string? Message)>> work, CancellationToken ct)
    {
        var (runId, publicId) = await db.InTransactionAsync(u => StartAsync(u, integration, mode, personId), ct);
        try
        {
            var result = await work(runId);
            await db.InTransactionAsync(u => FinishAsync(u, runId, "SUCCEEDED", result.Items, result.Findings, result.Message), ct);
        }
        catch (Exception ex)
        {
            var note = ex is IntegrationPermissionException or DomainException ? ex.Message : "The run failed; see the worker log for run " + runId + ".";
            await db.InTransactionAsync(u => FinishAsync(u, runId, "FAILED", 0, 0, note), CancellationToken.None);
            throw;
        }
        return publicId;
    }
}

/// <summary>Raises ops.Finding rows once per dedupe key while open, and queues the notice that goes with them.</summary>
public sealed class FindingWriter(OutboxWriter outbox, IClock clock)
{
    public sealed record NewFinding(string Type, string Severity, string Title, string? Details, int? FundingOwnerId = null, int? WorkloadId = null,
        string? ExternalId = null, string? Expected = null, string? Actual = null, long? SyncRunId = null, DateTime? DueUtc = null);

    /// <summary>Returns the new finding's ID, or null when an open finding with the same key already exists (its LastSeenUtc is refreshed).</summary>
    public async Task<long?> RaiseAsync(Uow uow, string dedupeKey, NewFinding f)
    {
        var existing = await uow.ScalarAsync<long?>("SELECT FindingId FROM ops.Finding WHERE DedupeKey = @dedupeKey AND Status IN ('OPEN','ACKNOWLEDGED')", new { dedupeKey });
        if (existing is long id)
        {
            await uow.ExecuteAsync("UPDATE ops.Finding SET LastSeenUtc = @now, Actual = COALESCE(@Actual, Actual), SyncRunId = COALESCE(@SyncRunId, SyncRunId) WHERE FindingId = @id",
                new { now = clock.UtcNow, f.Actual, f.SyncRunId, id });
            return null;
        }
        return await uow.ScalarAsync<long>(
            """
            INSERT ops.Finding (FindingType, Severity, FundingOwnerId, WorkloadId, ExternalId, Title, Details, Expected, Actual, Status, DedupeKey, SyncRunId, RaisedUtc, LastSeenUtc, DueUtc)
            OUTPUT inserted.FindingId
            VALUES (@Type, @Severity, @FundingOwnerId, @WorkloadId, @ExternalId, @Title, @Details, @Expected, @Actual, 'OPEN', @dedupeKey, @SyncRunId, @now, @now, @DueUtc)
            """,
            new
            {
                f.Type, f.Severity, f.FundingOwnerId, f.WorkloadId, ExternalId = Cut(f.ExternalId, 200), Title = Cut(f.Title, 300)!, Details = Cut(f.Details, 2000),
                Expected = Cut(f.Expected, 400), Actual = Cut(f.Actual, 400), dedupeKey = Cut(dedupeKey, 300)!, f.SyncRunId, now = clock.UtcNow, f.DueUtc,
            });
    }

    /// <summary>Resolves open findings of a type whose key was not seen in this run (the condition went away).</summary>
    public Task<int> ResolveMissingAsync(Uow uow, string findingType, IReadOnlyCollection<string> stillOpenKeys, string resolution)
        => uow.ExecuteAsync(
            $"""
            UPDATE ops.Finding SET Status = 'RESOLVED', ResolvedUtc = @now, Resolution = @resolution
            WHERE FindingType = @findingType AND Status IN ('OPEN','ACKNOWLEDGED')
              {(stillOpenKeys.Count == 0 ? "" : "AND DedupeKey NOT IN (SELECT value FROM OPENJSON(@keys))")}
            """,
            new { now = clock.UtcNow, resolution, findingType, keys = System.Text.Json.JsonSerializer.Serialize(stillOpenKeys) });

    public Task NotifyAsync(Uow uow, string code, long findingId, string subject, string text, IEnumerable<int> personIds, IEnumerable<RoleTarget> roles, IEnumerable<string>? queues = null, int? fundingOwnerId = null)
        => outbox.EnqueueAsync(uow, new OutboxMessage
        {
            EventType = code, FindingId = findingId, FundingOwnerId = fundingOwnerId, PersonIds = personIds.Distinct().ToList(), Roles = roles.ToList(),
            Queues = queues?.ToList() ?? new List<string>(), Subject = subject, Text = text, LinkPath = "/Operations/Drift",
        }, $"{code}:finding:{findingId}");

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
