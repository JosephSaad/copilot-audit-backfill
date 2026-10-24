using Governance.Application.Data;
using Governance.Application.Outbox;
using Microsoft.Data.SqlClient;

namespace Governance.Application.Commands;

/// <summary>
/// Shared plumbing for page commands: one transaction (system mode, after the caller authorized), an
/// idempotency key per form post (wf.CommandLog), and an audit row for every change.
/// </summary>
public sealed class CommandRunner(Db db, ICurrentPrincipal principal, AuditWriter audit)
{
    public ICurrentPrincipal Principal => principal;

    /// <summary>
    /// Runs the command once per idempotency key. A double click or a retried post returns the first result
    /// (ResultRef) without a second effect.
    /// </summary>
    public Task<string?> RunAsync(Guid idempotencyKey, string command, Func<Uow, Task<string?>> work, string? auditEntityType = null, CancellationToken ct = default)
        => db.InTransactionAsync(async uow =>
        {
            if (idempotencyKey != Guid.Empty)
            {
                var prior = await uow.QuerySingleOrDefaultAsync<string?>(
                    "SELECT ResultRef FROM wf.CommandLog WITH (UPDLOCK, HOLDLOCK) WHERE IdempotencyKey = @idempotencyKey", new { idempotencyKey });
                if (prior is not null || await uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.CommandLog WHERE IdempotencyKey = @idempotencyKey", new { idempotencyKey }) > 0)
                    return prior;
            }
            var result = await work(uow);
            if (idempotencyKey != Guid.Empty)
            {
                try
                {
                    await uow.ExecuteAsync("INSERT wf.CommandLog (IdempotencyKey, PersonId, Command, ResultRef) VALUES (@idempotencyKey, @p, @command, @result)",
                        new { idempotencyKey, p = principal.PersonId, command, result });
                }
                catch (SqlException ex) when (ex.Number is 2627 or 2601)
                {
                    throw new DomainException("This form was already sent.");
                }
            }
            if (auditEntityType is not null)
                await audit.WriteAsync(uow, principal, command, auditEntityType, result, null);
            return result;
        }, ct);

    public Task RunAsync(Guid idempotencyKey, string command, Func<Uow, Task> work, CancellationToken ct = default)
        => RunAsync(idempotencyKey, command, async u => { await work(u); return (string?)null; }, null, ct);

    public Task AuditAsync(Uow uow, string action, string? entityType, object? entityId, string? detail = null)
        => audit.WriteAsync(uow, principal, action, entityType, entityId, detail);

    /// <summary>Throws a safe refusal when the principal lacks the permission (tenant-wide or for this funding owner).</summary>
    public void Require(string permission, int? fundingOwnerId = null, string? what = null)
    {
        if (!principal.Can(permission, fundingOwnerId))
            throw new DomainException(what ?? "You don't have the role this action needs.");
    }

    public void RequireAny(params string[] permissions)
    {
        if (!permissions.Any(p => principal.Can(p)))
            throw new DomainException("You don't have the role this action needs.");
    }

    public static string Trim(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s.Trim() : s.Trim()[..max]);
    public static string? TrimN(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : Trim(s, max);
}
