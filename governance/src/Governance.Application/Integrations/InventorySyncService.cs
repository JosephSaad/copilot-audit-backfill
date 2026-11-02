using System.Text.Json;
using System.Text.Json.Nodes;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Application.Integrations;

/// <summary>
/// X4: the Power Platform inventory under the operator's delegated token (the API refuses app-only, R02).
/// Environments, Copilot Studio agents (with harness and owner) and agent flows, paged with skip tokens, are kept
/// as a snapshot and compared with the register: unregistered items, owner, harness and environment type changes.
/// Nothing is created in the register: the web's Discovery page claims items.
/// </summary>
public sealed class InventorySyncService(Db db, IPowerPlatformApi api, FindingWriter findings, BusinessCalendarProvider calendars, IClock clock) : IInventorySync
{
    public static readonly (string Type, string BindingType, string FindingType, string Label)[] Kinds =
    {
        ("microsoft.powerplatform/environments", "PP_ENVIRONMENT", FindingTypes.UnregisteredEnvironment, "environment"),
        ("microsoft.copilotstudio/agents", "CS_AGENT", FindingTypes.UnregisteredAgent, "agent"),
        ("microsoft.powerautomate/agentflows", "AGENT_FLOW", FindingTypes.UnregisteredFlow, "agent flow"),
    };

    public sealed record Item(string ResourceType, string ResourceId, string? DisplayName, string? EnvironmentId, string? EnvironmentType, string? OwnerObjectId, string? Harness, DateTime? CreatedAt, string RawJson);

    public Task<Guid> RunAsync(string delegatedAccessToken, int operatorPersonId, CancellationToken ct)
        => SyncRuns.RunAsync(db, IntegrationCodes.Inventory, "DELEGATED", operatorPersonId, async runId =>
        {
            // Every page is read with no transaction open; the snapshot is then written in one short transaction.
            var items = new List<Item>();
            foreach (var k in Kinds) items.AddRange(await ReadAllAsync(delegatedAccessToken, k.Type, ct));
            return await db.InTransactionAsync(uow => WriteAsync(uow, runId, items), ct);
        }, ct);

    private async Task<(int Items, int Findings, string? Message)> WriteAsync(Uow uow, long runId, List<Item> items)
    {
            // Environment names and types for agents and flows.
            var envs = items.Where(i => i.ResourceType == Kinds[0].Type).ToDictionary(i => i.ResourceId, StringComparer.OrdinalIgnoreCase);
            foreach (var i in items)
                await uow.ExecuteAsync(
                    """
                    INSERT intg.InventoryItem (SyncRunId, ResourceType, ResourceId, DisplayName, EnvironmentId, EnvironmentName, EnvironmentType, OwnerObjectId, Harness, CreatedAt, RawJson)
                    VALUES (@runId, @ResourceType, @ResourceId, @DisplayName, @EnvironmentId, @EnvName, @EnvironmentType, @OwnerObjectId, @Harness, @CreatedAt, @RawJson)
                    """,
                    new
                    {
                        runId, i.ResourceType, ResourceId = Cut(i.ResourceId, 200), DisplayName = Cut(i.DisplayName, 400), EnvironmentId = Cut(i.EnvironmentId, 200),
                        EnvName = i.EnvironmentId is not null && envs.TryGetValue(i.EnvironmentId, out var e) ? Cut(e.DisplayName, 400) : null,
                        EnvironmentType = Cut(i.EnvironmentType ?? (i.EnvironmentId is not null && envs.TryGetValue(i.EnvironmentId, out var e2) ? e2.EnvironmentType : null), 60),
                        OwnerObjectId = Cut(i.OwnerObjectId, 100), Harness = Cut(i.Harness, 100), i.CreatedAt, i.RawJson,
                    });
            var raised = await EvaluateAsync(uow, runId, items);
            return (items.Count, raised, $"{items.Count(i => i.ResourceType == Kinds[0].Type)} environments, {items.Count(i => i.ResourceType == Kinds[1].Type)} agents, {items.Count(i => i.ResourceType == Kinds[2].Type)} agent flows; {raised} new findings.");
    }

    private async Task<List<Item>> ReadAllAsync(string token, string type, CancellationToken ct)
    {
        var result = new List<Item>();
        string? skip = null;
        var guard = 0;
        do
        {
            var body = QueryBody(type, skip);
            var page = await api.QueryResourcesAsync(token, body, ct);
            result.AddRange(page.Data.Select(d => Parse(type, d)));
            skip = string.IsNullOrEmpty(page.SkipToken) ? null : page.SkipToken;
        } while (skip is not null && ++guard < 500);
        return result;
    }

    public static string QueryBody(string type, string? skipToken)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["TableName"] = "PowerPlatformResources",
            ["Clauses"] = new object[]
            {
                new Dictionary<string, object?> { ["$type"] = "where", ["FieldName"] = "type", ["Operator"] = "==", ["Values"] = new[] { $"'{type}'" } },
            },
            ["Options"] = new Dictionary<string, object?> { ["Top"] = 1000, ["Skip"] = 0, ["SkipToken"] = skipToken ?? "" },
        });

    /// <summary>Reads the fields we need from a resource whatever its exact shape; the raw row is kept as evidence.</summary>
    public static Item Parse(string type, JsonElement row)
    {
        var node = JsonNode.Parse(row.GetRawText())!;
        string? S(params string[] paths)
        {
            foreach (var p in paths)
            {
                JsonNode? cur = node;
                foreach (var part in p.Split('.')) { cur = cur?[part]; if (cur is null) break; }
                if (cur is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }
        var id = S("name", "id", "properties.id") ?? Guid.NewGuid().ToString();
        DateTime? created = DateTime.TryParse(S("properties.createdAt", "properties.createdTime"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var c) ? c : null;
        return new Item(type, id,
            S("properties.displayName", "displayName", "name"),
            type == Kinds[0].Type ? id : S("properties.environmentId", "environmentId"),
            S("properties.environmentType", "properties.environmentSku", "environmentType"),
            S("properties.ownerId", "properties.owner.id", "properties.createdBy.id", "properties.createdBy", "properties.ownerObjectId"),
            S("properties.harness", "harness"),
            created, row.GetRawText());
    }

    private async Task<int> EvaluateAsync(Uow uow, long runId, IReadOnlyList<Item> items)
    {
        var raised = 0;
        var calendar = await calendars.GetAsync(uow);
        var claimDue = calendar.AddBusinessDays(clock.UtcNow, 10);
        var previousRun = await uow.ScalarAsync<long?>(
            "SELECT TOP (1) SyncRunId FROM intg.SyncRun WHERE Integration = 'X4' AND Status = 'SUCCEEDED' AND SyncRunId <> @runId ORDER BY SyncRunId DESC", new { runId });
        var previous = previousRun is long pr
            ? (await uow.QueryAsync<Item>("SELECT ResourceType, ResourceId, DisplayName, EnvironmentId, EnvironmentType, OwnerObjectId, Harness, CreatedAt, CAST(N'' AS nvarchar(max)) AS RawJson FROM intg.InventoryItem WHERE SyncRunId = @pr", new { pr }))
                .GroupBy(i => (i.ResourceType, i.ResourceId.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<(string, string), Item>();
        var openKeys = new List<string>();

        foreach (var k in Kinds)
        {
            var bound = (await uow.QueryAsync<(string ExternalId, int WorkloadId, int FundingOwnerId)>(
                "SELECT ExternalId, WorkloadId, FundingOwnerId FROM core.WorkloadBinding WHERE ObjectType = @BindingType AND EffectiveTo IS NULL", new { k.BindingType }))
                .GroupBy(b => b.ExternalId.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            foreach (var i in items.Where(x => x.ResourceType == k.Type))
            {
                var key = i.ResourceId.ToLowerInvariant();
                bound.TryGetValue(key, out var b);
                if (b == default)
                {
                    var dedupe = $"UNREG:{k.BindingType}:{key}";
                    openKeys.Add(dedupe);
                    var ownerPerson = Guid.TryParse(i.OwnerObjectId, out var oid) ? await uow.ScalarAsync<int?>("SELECT PersonId FROM sec.Person WHERE EntraObjectId = @oid AND AccountEnabled = 1", new { oid }) : null;
                    var id = await findings.RaiseAsync(uow, dedupe, new FindingWriter.NewFinding(k.FindingType, "MEDIUM",
                        $"Unregistered {k.Label}: {i.DisplayName ?? i.ResourceId}",
                        $"Found by inventory sync and not in the register. The owner claims and classifies it within 10 business days, or Platform Operations does.",
                        ExternalId: i.ResourceId, Actual: i.Harness is null ? null : $"harness {i.Harness}", SyncRunId: runId, DueUtc: claimDue));
                    if (id is long fid)
                    {
                        raised++;
                        await findings.NotifyAsync(uow, "N11", fid, $"Unregistered {k.Label}: {i.DisplayName ?? i.ResourceId}",
                            "Claim and classify it in the Workbench within 10 business days (Registry > Discovery).",
                            ownerPerson is int op ? new[] { op } : Array.Empty<int>(), new[] { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) });
                    }
                    continue;
                }
                if (!previous.TryGetValue((k.Type, key), out var before)) continue;
                raised += await ChangeAsync(uow, runId, FindingTypes.OwnerChange, "owner", k.Label, i, before.OwnerObjectId, i.OwnerObjectId, b.WorkloadId, b.FundingOwnerId);
                raised += await ChangeAsync(uow, runId, FindingTypes.HarnessChange, "harness", k.Label, i, before.Harness, i.Harness, b.WorkloadId, b.FundingOwnerId);
                raised += await ChangeAsync(uow, runId, FindingTypes.EnvironmentTypeChange, "environment type", k.Label, i, before.EnvironmentType, i.EnvironmentType, b.WorkloadId, b.FundingOwnerId);
            }
        }
        foreach (var k in Kinds)
            await findings.ResolveMissingAsync(uow, k.FindingType, openKeys.Where(x => x.StartsWith($"UNREG:{k.BindingType}:", StringComparison.Ordinal)).ToList(),
                "Registered, claimed or no longer in the inventory.");
        return raised;
    }

    private async Task<int> ChangeAsync(Uow uow, long runId, string type, string what, string label, Item i, string? before, string? now, int workloadId, int fundingOwnerId)
    {
        if (string.Equals(before ?? "", now ?? "", StringComparison.OrdinalIgnoreCase)) return 0;
        var id = await findings.RaiseAsync(uow, $"{type}:{i.ResourceId.ToLowerInvariant()}:{now}", new FindingWriter.NewFinding(type,
            type == FindingTypes.HarnessChange ? "HIGH" : "MEDIUM", $"The {what} of a registered {label} changed: {i.DisplayName ?? i.ResourceId}",
            $"Inventory shows a different {what} than the last sync. Check it against the approved workload; a material change opens review (I3).",
            FundingOwnerId: fundingOwnerId, WorkloadId: workloadId, ExternalId: i.ResourceId, Expected: before, Actual: now, SyncRunId: runId));
        if (id is not long fid) return 0;
        await findings.NotifyAsync(uow, "N10", fid, $"Drift: {what} changed on {i.DisplayName ?? i.ResourceId}", "A registered item changed outside an approved request.",
            Array.Empty<int>(), new[] { new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) }, new[] { "OPERATIONS" }, fundingOwnerId);
        return 1;
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
