using System.Text.Json;
using Governance.Application.Data;

namespace Governance.Application.Outbox;

/// <summary>
/// A notification or integration command written in the same transaction as the state change (transactional
/// outbox), so it is never lost and never sent for a rolled-back change. The worker's dispatcher delivers it.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>N01..N22, or an integration command such as <c>ITSM.Create</c>.</summary>
    public string EventType { get; set; } = "";
    public long? RequestId { get; set; }
    public long? TaskId { get; set; }
    public long? FindingId { get; set; }
    public long? DecisionId { get; set; }
    public int? EventId { get; set; }
    public long? ItsmTicketId { get; set; }
    public int? FundingOwnerId { get; set; }
    /// <summary>People to notify directly (requester, sponsor, configurer...).</summary>
    public List<int> PersonIds { get; set; } = new();
    /// <summary>Notify every eligible holder of these roles in this scope (task assignees, queues).</summary>
    public List<RoleTarget> Roles { get; set; } = new();
    /// <summary>Team queues for a Teams channel card (INTAKE, ARCHITECTURE, OPERATIONS, FINOPS, RISK).</summary>
    public List<string> Queues { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public string? LinkPath { get; set; }
    /// <summary>Informed parties get a digest line instead of an immediate notice.</summary>
    public bool DigestOnly { get; set; }
    /// <summary>Activity feed type in the Teams app manifest (taskAssigned, requestReturned, decisionRecorded...).</summary>
    public string? ActivityType { get; set; }
    public Dictionary<string, string> Facts { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static OutboxMessage FromJson(string json) => JsonSerializer.Deserialize<OutboxMessage>(json, Json) ?? new OutboxMessage();
}

public sealed record RoleTarget(string RoleCode, string ScopeType, int? ScopeId);

public sealed class OutboxWriter
{
    /// <summary>Queues the message once per dedupe key; a repeat with the same key is ignored.</summary>
    public async Task<bool> EnqueueAsync(Uow uow, OutboxMessage message, string dedupeKey, DateTime? availableAtUtc = null)
    {
        var rows = await uow.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM ntf.Outbox WHERE DedupeKey = @DedupeKey)
                INSERT ntf.Outbox (EventType, Payload, DedupeKey, AvailableAtUtc)
                VALUES (@EventType, @Payload, @DedupeKey, COALESCE(@AvailableAtUtc, SYSUTCDATETIME()));
            """,
            new { message.EventType, Payload = message.ToJson(), DedupeKey = dedupeKey, AvailableAtUtc = availableAtUtc });
        return rows > 0;
    }

    public Task AddDigestAsync(Uow uow, int personId, string ruleCode, string text, string? link)
        => uow.ExecuteAsync("INSERT ntf.DigestEntry (PersonId, RuleCode, Text, Link) VALUES (@personId, @ruleCode, @text, @link)",
            new { personId, ruleCode, text = Trim(text, 1000), link });

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Append-only audit trail (aud.AuditLog is a ledger table). Never holds tokens or secrets.</summary>
public sealed class AuditWriter(IClock clock)
{
    public Task WriteAsync(Uow uow, ICurrentPrincipal who, string action, string? entityType, object? entityId, string? detail, string outcome = "OK", string? correlationId = null)
        => uow.ExecuteAsync(
            """
            INSERT aud.AuditLog (Utc, PersonId, ActingAsPersonId, Action, EntityType, EntityId, Detail, CorrelationId, Outcome)
            VALUES (@Utc, @PersonId, @ActingAs, @Action, @EntityType, @EntityId, @Detail, @CorrelationId, @Outcome)
            """,
            new
            {
                Utc = clock.UtcNow,
                PersonId = who.RealPersonId ?? (who.PersonId == 0 ? (int?)null : who.PersonId),
                ActingAs = who.RealPersonId is null ? (int?)null : who.PersonId,
                Action = action,
                EntityType = entityType,
                EntityId = entityId?.ToString(),
                Detail = detail is null ? null : (detail.Length > 2000 ? detail[..2000] : detail),
                CorrelationId = correlationId,
                Outcome = outcome,
            });
}
