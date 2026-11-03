using Governance.Application.Data;
using Governance.Application.Workflow;
using Microsoft.Extensions.Logging;

namespace Governance.Application.Integrations;

/// <summary>
/// Reads the state of open ITSM tickets (every 5 minutes, the ItsmPoller job). A closed fulfillment ticket moves its
/// request to verification (never to Completed), with the configurer mapped from the ticket's "closed by" (SoD3).
/// Each ticket stands alone: one that cannot be read is recorded on the ticket (PollError) and the poll goes on with
/// the next. A ticket the ITSM no longer has is marked NOT_FOUND for an operator to review; it is still polled, so it
/// picks up its state again should it reappear.
/// </summary>
public sealed class ItsmPollService(Db db, IItsmClient itsm, WorkflowEngine engine, ILogger<ItsmPollService> log)
{
    public const string NotFound = "NOT_FOUND";

    public sealed record Result(int Polled, int Closed, int NotFound, int Failed)
    {
        public string Message => $"{Polled} open tickets polled, {Closed} closed."
            + (NotFound > 0 ? $" {NotFound} no longer in the ITSM (marked {ItsmPollService.NotFound} for review)." : "")
            + (Failed > 0 ? $" {Failed} could not be read (see the ticket's poll error and the worker log)." : "");
    }

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        var open = (await db.InTransactionAsync(u => u.QueryAsync<(long Id, string Number, string State)>(
            "SELECT ItsmTicketId, ExternalNumber, State FROM wf.ItsmTicket WHERE ExternalNumber IS NOT NULL AND State NOT IN ('CLOSED','CANCELLED') ORDER BY ItsmTicketId"), ct)).ToList();
        int closed = 0, notFound = 0, failed = 0;
        foreach (var t in open)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await itsm.GetAsync(t.Number, ct);
                await db.InTransactionAsync(async u =>
                {
                    await u.ExecuteAsync(
                        """
                        UPDATE wf.ItsmTicket SET State = @State, LastPolledUtc = SYSUTCDATETIME(), ClosureNotes = COALESCE(@notes, ClosureNotes),
                               ClosedUtc = CASE WHEN @State IN ('CLOSED','CANCELLED') THEN COALESCE(@ClosedUtc, SYSUTCDATETIME()) END,
                               PollError = NULL, PollErrorUtc = NULL
                        WHERE ItsmTicketId = @Id
                        """,
                        new { state.State, notes = state.ClosureNotes is { Length: > 2000 } n ? n[..2000] : state.ClosureNotes, state.ClosedUtc, t.Id });
                    if (state.State is "CLOSED" or "CANCELLED" && t.State is not ("CLOSED" or "CANCELLED"))
                    {
                        var configurer = state.ClosedByUpn is null ? null : await u.ScalarAsync<int?>(
                            "SELECT TOP (1) PersonId FROM sec.Person WHERE UserPrincipalName = @upn OR Mail = @upn ORDER BY CASE WHEN UserPrincipalName = @upn THEN 0 ELSE 1 END", new { upn = state.ClosedByUpn });
                        await engine.OnTicketClosedAsync(u, t.Id, state.State, configurer, state.ClosureNotes);
                        closed++;
                    }
                }, ct);
            }
            catch (ItsmTicketNotFoundException ex)
            {
                notFound++;
                log.LogWarning("ITSM ticket {Number} (ticket {Id}) was not found; marked {State} for review.", t.Number, t.Id, NotFound);
                await RecordAsync(t.Id, NotFound, ex.Message + " It may have been deleted or moved out of the integration account's reach: check it in the ITSM.");
            }
            // A refused credential refuses every ticket: the run fails, so the job shows it and N19 follows.
            catch (Exception ex) when (ex is not IntegrationPermissionException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
            {
                failed++;
                var reference = Convert.ToHexString(Guid.NewGuid().ToByteArray(), 0, 3);
                log.LogError(ex, "ITSM ticket {Number} (ticket {Id}) could not be polled. Reference {Reference}.", t.Number, t.Id, reference);
                var note = ex is DomainException ? ex.Message : $"{ex.GetType().Name}, reference {reference}: the details are in the worker's log under it.";
                await RecordAsync(t.Id, null, note);
            }
        }
        var result = new Result(open.Count, closed, notFound, failed);
        // Not one ticket could be read: the ITSM itself is failing, so the run fails (N19 after two in a row).
        if (failed > 0 && failed == open.Count) throw new InvalidOperationException(result.Message);
        return result;
    }

    /// <summary>Records why the ticket could not be read; with a state, also sets it (NOT_FOUND).</summary>
    private Task RecordAsync(long id, string? state, string error)
        => db.InTransactionAsync(u => u.ExecuteAsync(
            """
            UPDATE wf.ItsmTicket SET PollError = @error, PollErrorUtc = SYSUTCDATETIME(), LastPolledUtc = SYSUTCDATETIME(), State = COALESCE(@state, State)
            WHERE ItsmTicketId = @id
            """, new { id, state, error = error.Length <= 400 ? error : error[..400] }), CancellationToken.None);
}
