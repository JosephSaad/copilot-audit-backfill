using System.Globalization;
using CopilotInsights.Data;
using Dapper;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The dashboard's background sender. Every few minutes, on one web server at a time (a SQL
/// application lock), it checks the pipeline and the data, raises alerts, and sends the digests
/// that are due. Alerts go by email to the recipients set in Admin › Alerts and digests, and to
/// the Windows Application log; each fires once per occurrence (dash.AlertState).
/// </summary>
public sealed partial class Notifier(IServiceScopeFactory scopes, IOptions<InsightsOptions> options, ILogger<Notifier> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Notifications) { log.LogInformation("Notifications are off in the site's configuration."); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.NotifierStartDelaySeconds)), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            /* Only the site stopping ends the loop. A timeout is an OperationCanceledException too, and one that escaped
               here stopped the whole site: a mail server or token endpoint that did not answer took it down. */
            try { await RunAllAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { log.LogError(ex, "The notification checks failed"); }
            try { await Task.Delay(Interval, stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One round of checks in every tenant's database.</summary>
    public async Task<List<string>> RunAllAsync(CancellationToken ct = default)
    {
        using var root = scopes.CreateScope();
        var tenants = root.ServiceProvider.GetRequiredService<TenantAccessor>();
        var done = new List<string>();
        var before = tenants.Current;   // run from a page: the page's tenant again afterwards, for its audit entry
        try
        {
            foreach (var t in tenants.All)
            {
                tenants.Use(t);
                try { done.AddRange((await RunOnceAsync(ct)).Select(d => tenants.Many ? $"[{t.Name}] {d}" : d)); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { log.LogError(ex, "The notification checks failed for {Tenant}", t.Name); }
            }
        }
        finally { tenants.Use(before); }
        return done;
    }

    /// <summary>One round of checks in the current tenant's database. Returns what it did, for Admin › Alerts and digests' "Run the checks now".</summary>
    public async Task<List<string>> RunOnceAsync(CancellationToken ct = default)
    {
        var done = new List<string>();
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Db>();
        if (await db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.AlertState', N'U') IS NULL THEN 0 ELSE 1 END") == 0)
        {
            done.Add("The operations schema is not installed (Create-CopilotOperationsSchema.sql), so there is nothing to check.");
            return done;
        }
        await using var conn = db.Open();
        var got = await conn.ExecuteScalarAsync<int>("DECLARE @r INT; EXEC @r = sp_getapplock @Resource = N'CopilotInsightsNotifier', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;");
        if (got < 0) { done.Add("Another web server is running the checks."); return done; }
        try
        {
            var run = new Run(sp, done, ct);
            await run.HereAsync();
            await run.PipelineAsync();
            await run.FreshnessAsync();
            await run.AfterRefreshAsync();
            await run.DigestsAsync();
            await run.CertificateAsync();
            await run.ManagerDigestsAsync();
            await run.ExtractsAsync();
        }
        finally
        {
            /* A lock whose session has dropped is gone already, and releasing it again raised an error that threw away the
               round's report. What the round sent is safe without the lock: each send claims its row first. */
            try { await conn.ExecuteAsync("EXEC sp_releaseapplock @Resource = N'CopilotInsightsNotifier', @LockOwner = 'Session';"); }
            catch (Exception ex) { log.LogWarning(ex, "The notification lock could not be released; its session had ended, which releases it."); }
        }
        return done;
    }

    sealed partial class Run(IServiceProvider sp, List<string> done, CancellationToken ct)
    {
        readonly Db _db = sp.GetRequiredService<Db>();
        readonly Settings _settings = sp.GetRequiredService<Settings>();
        readonly OpsData _ops = sp.GetRequiredService<OpsData>();
        readonly OpsReports _reports = sp.GetRequiredService<OpsReports>();
        readonly Mailer _mail = sp.GetRequiredService<Mailer>();
        readonly ILogger _log = sp.GetRequiredService<ILogger<Notifier>>();

        Task<string?> StateAsync(string key) => _db.ScalarAsync<string?>("SELECT LastValue FROM dash.AlertState WHERE AlertKey = @key", new { key });
        Task SetStateAsync(string key, string? value) => _db.ExecuteAsync(@"
UPDATE dash.AlertState SET LastValue = @value, LastFiredUtc = SYSUTCDATETIME() WHERE AlertKey = @key;
IF @@ROWCOUNT = 0 INSERT INTO dash.AlertState (AlertKey, LastValue) VALUES (@key, @value);", new { key, value });

        /// <summary>
        /// Moves a state from what the check saw to <paramref name="value"/>, and only if it is still what the check saw:
        /// the claim an alert makes before anything is sent. False when another web server moved it first. Before, the state
        /// was written after the email and the webhook had gone, so a database that could be read but not written (a full log)
        /// sent the same alert every five minutes, and two servers whose lock had slipped could both send it.
        /// </summary>
        async Task<bool> ClaimStateAsync(string key, string? seen, string? value)
        {
            try
            {
                return await _db.ExecuteAsync(@"
UPDATE dash.AlertState SET LastValue = @value, LastFiredUtc = SYSUTCDATETIME()
WHERE AlertKey = @key AND (LastValue = @seen OR (LastValue IS NULL AND @seen IS NULL));
IF @@ROWCOUNT = 0 AND @seen IS NULL
    INSERT INTO dash.AlertState (AlertKey, LastValue) SELECT @key, @value
    WHERE NOT EXISTS (SELECT 1 FROM dash.AlertState WITH (UPDLOCK, HOLDLOCK) WHERE AlertKey = @key);", new { key, seen, value }) == 1;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2627 or 2601) { return false; }   // inserted by another server first
        }

        Task ForgetAsync(string key) => _db.ExecuteAsync("DELETE FROM dash.AlertState WHERE AlertKey = @key", new { key });

        /// <summary>
        /// An alert whose email failed waits under retry:{name}: the failed tries so far, and whether its next is not
        /// due yet. A wait older than a day is forgotten.
        /// </summary>
        async Task<(byte Tries, bool Waiting)> RetryAsync(string name)
        {
            var r = (await _db.QueryAsync<(string? Tries, DateTime AtUtc)>("SELECT LastValue, LastFiredUtc FROM dash.AlertState WHERE AlertKey = @k", new { k = "retry:" + name })).FirstOrDefault();
            if (r.Tries is null || r.AtUtc < DateTime.UtcNow.AddDays(-1) || !byte.TryParse(r.Tries, out var tries)) return (0, false);
            return (tries, !Retry.Ready(r.AtUtc + Retry.Wait, DateTime.UtcNow));
        }

        /// <summary>
        /// Raises an alert once per occurrence: its state moves to <paramref name="value"/> before anything is sent,
        /// and only from what the check saw (<paramref name="seen"/>). An email the server refused or did not answer, with no
        /// webhook to take the alert either, is tried again an hour later, three tries in all, as digests are; before, the
        /// state moved on whatever happened, so that day's security alert or that pipeline failure was never emailed. True
        /// when the state has moved on; false when the alert waits for another try or another server raised it.
        /// </summary>
        async Task<bool> RaiseAsync(string key, string? seen, string? value, string kind, string recipientsSetting, string subject, IReadOnlyList<string> lines, string? link = null,
                                    IReadOnlyList<string>? alsoTo = null)
        {
            var (tries, waiting) = await RetryAsync(key);
            if (waiting) return false;
            if (!await ClaimStateAsync(key, seen, value)) return false;
            return await SendOnceAsync(key, tries, () => SetStateAsync(key, seen), kind, recipientsSetting, subject, lines, link, alsoTo);
        }

        /// <summary>Sends a claimed alert, and puts the claim back (<paramref name="release"/>) when it is to be tried again.</summary>
        async Task<bool> SendOnceAsync(string name, byte tries, Func<Task> release, string kind, string recipientsSetting, string subject, IReadOnlyList<string> lines, string? link,
                                       IReadOnlyList<string>? alsoTo = null)
        {
            bool delivered;
            /* Nothing has gone when this throws: everything is read before the email, an email the site was stopped during
               is not sent (Mailer), and nothing after the email throws. So the claim is given back, for the next round. */
            try { delivered = await AlertAsync(kind, recipientsSetting, subject, lines, link, eventLog: tries == 0, alsoTo); }
            catch (Exception) { await release(); throw; }
            var next = Retry.After(delivered, tries, DateTime.UtcNow);
            if (!next.Retry) { await ForgetAsync("retry:" + name); return true; }
            await release();
            await SetStateAsync("retry:" + name, next.FailedTries.ToString(CultureInfo.InvariantCulture));
            done.Add($"Trying the alert email again after {next.RetryAfterUtc:HH:mm} UTC (try {next.FailedTries + 1} of {Retry.Tries}): {subject}");
            return false;
        }

        /// <summary>
        /// Emails the alert to the setting's recipients, writes it to the Windows Application log (the first try only) and
        /// posts it to the webhook. True when it reached someone, or when sending again would change nothing: email off or
        /// no recipients.
        /// </summary>
        async Task<bool> AlertAsync(string kind, string recipientsSetting, string subject, IReadOnlyList<string> lines, string? link, bool eventLog, IReadOnlyList<string>? alsoTo = null)
        {
            var email = new Email(subject);
            foreach (var l in lines) email.Paragraph(l);
            var url = await _settings.StringAsync("DashboardUrl");
            if (url is not null && link is not null) email.Link(url.TrimEnd('/') + link, "Open it in Copilot Insights");
            email.Footer(null);
            /* One setting, or several separated by commas: their addresses together. */
            var to = new List<string>();
            foreach (var setting in recipientsSetting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (await _settings.StringAsync(setting) is { } v) to.Add(v);
            /* Addresses the alert names itself, such as a spend limit's executives. */
            if (alsoTo is not null) to.AddRange(alsoTo);
            var toLog = eventLog && await _settings.StringAsync("EventLogAlerts") != "0";
            var webhook = await sp.GetRequiredService<SecretStore>().GetAsync(SecretStore.AlertWebhookUrl);
            var format = await _settings.StringAsync("AlertWebhookFormat") is "Json" ? "Json" : "Teams";
            var (status, error) = await _mail.DeliverAsync(kind, MailConfig.Recipients(string.Join(";", to)), subject, email.Html(), email.Text(), ct);
            if (toLog) WriteEventLog(kind, subject + Environment.NewLine + string.Join(Environment.NewLine, lines));
            var posted = await WebhookAsync(webhook, format, kind, subject, lines, url is not null && link is not null ? url.TrimEnd('/') + link : null);
            done.Add(error is null ? $"Alert sent: {subject}" : $"Alert recorded, not emailed ({error}): {subject}");
            return Mailer.Settled(status) || posted;
        }

        void WriteEventLog(string kind, string message)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var id = kind switch { "Pipeline" => 1001, "Freshness" => 1002, "Certificate" => 1003, "Security" => 2001, "Anomaly" => 3001, "Pool" => 4001, "Leavers" => 4002, "Commitment" => 4003, "Budget" => 4004, "NonHuman" => 2002, "NewAgent" => 5001, _ => 1000 };
                System.Diagnostics.EventLog.WriteEntry("Copilot Insights", message.Length > 30000 ? message[..30000] : message,
                    kind is "Security" or "NonHuman" ? System.Diagnostics.EventLogEntryType.Warning : System.Diagnostics.EventLogEntryType.Information, id);
            }
            catch (Exception ex) { _log.LogDebug(ex, "The Windows event log source 'Copilot Insights' is not registered; the installer creates it."); }
        }

        /* ---- The pipeline ---------------------------------------------------------------------- */

        public async Task PipelineAsync()
        {
            var runs = await _ops.PipelineRunsAsync(5);
            if (runs.Count == 0) return;
            var seenText = await StateAsync("pipeline");
            var seen = int.TryParse(seenText, out var s) ? s : 0;
            /* One failed run at a time, the state moving to each before it is sent; a run whose email waits for another try holds back the ones after it. */
            var caughtUp = true;
            foreach (var r in runs.Where(r => r.RunId > seen && r.Status == "Failed").OrderBy(r => r.RunId))
            {
                var id = r.RunId.ToString(CultureInfo.InvariantCulture);
                if (!await RaiseAsync("pipeline", seenText, id, "Pipeline", "AlertAdminTo", $"Copilot pipeline failed: {r.FailedStep}",
                        [$"The {r.Kind.ToLowerInvariant()} run that started {Zones.Stamp(r.StartedUtc, await _ops.ZoneAsync())} on {r.Host} stopped at \"{r.FailedStep}\". The next run resumes it; to resume now, run Invoke-CopilotPipeline.ps1 on the job server.",
                         r.Detail is null ? "" : "Its last lines:\n" + r.Detail], "/admin/health")) { caughtUp = false; break; }
                seenText = id;
            }
            var stuck = runs.FirstOrDefault(r => r.Status == "Running" && r.StartedUtc < DateTime.UtcNow.AddHours(-12));
            if (stuck is not null && await StateAsync("pipeline-stuck") is var stuckSeen && stuckSeen != stuck.RunId.ToString(CultureInfo.InvariantCulture))
                await RaiseAsync("pipeline-stuck", stuckSeen, stuck.RunId.ToString(CultureInfo.InvariantCulture), "Pipeline", "AlertAdminTo", "Copilot pipeline has not finished in 12 hours",
                    [$"The {stuck.Kind.ToLowerInvariant()} run that started {Zones.Stamp(stuck.StartedUtc, await _ops.ZoneAsync())} on {stuck.Host} is still marked as running. A reboot or a killed task leaves it like this; check the log on the job server."], "/admin/health");
            if (!caughtUp) return;
            var at = int.TryParse(seenText, out var a) ? a : 0;
            var max = runs.Where(r => r.Status != "Running").Select(r => r.RunId).DefaultIfEmpty(at).Max();
            if (max > at) await SetStateAsync("pipeline", max.ToString(CultureInfo.InvariantCulture));
        }

        public async Task FreshnessAsync()
        {
            var (last, age, limit) = await _ops.FreshnessAsync();
            if (last is null || age <= limit) return;
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var seen = await StateAsync("freshness");
            if (seen == today) return;
            await RaiseAsync("freshness", seen, today, "Freshness", "AlertAdminTo", $"Copilot Insights data is {age:0} hours old",
                [$"The dashboard's data was last refreshed {Zones.Stamp(last, await _ops.ZoneAsync())}, more than {limit} hours ago. The nightly task on the job server may not be running: check Task Scheduler and Admin › Health."], "/admin/health");
        }

        /* ---- After each refresh: security, drops and spikes, agents, the pool, leavers ------------ */

        public async Task AfterRefreshAsync()
        {
            var runId = await _db.ScalarAsync<int?>("SELECT MAX(RunId) FROM dash.RefreshRun;");
            if (runId is null) return;
            var last = await StateAsync("refresh");
            /* Done for this refresh, unless one of its alerts waits to be emailed again: each check below keeps its own state, so running them again repeats nothing. */
            if (last == runId.Value.ToString(CultureInfo.InvariantCulture)
                && await _db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.AlertState WHERE AlertKey IN (N'retry:security', N'retry:anomaly', N'retry:agents', N'retry:pool', N'retry:leavers', N'retry:nonhuman', N'retry:commitment', N'retry:budget', N'retry:limits') AND LastFiredUtc > DATEADD(DAY, -1, SYSUTCDATETIME());") == 0)
                return;
            var first = last is null;

            await SecurityAsync();
            await AnomaliesAsync();
            await AgentsAsync(first);
            await PoolAsync();
            await CommitmentAsync();
            await BudgetAsync();
            await SpendLimitsAsync();
            await AgentPoliciesAsync();
            await LeaversAsync(first);
            await NonHumanAsync();
            await SetStateAsync("refresh", runId.Value.ToString(CultureInfo.InvariantCulture));
        }

        async Task SecurityAsync()
        {
            var (day, signals) = await _ops.SecurityDayAsync();
            if (day is null) return;
            var key = day.Value.ToString("yyyy-MM-dd");
            var seen = await StateAsync("security");
            if (seen == key) return;
            var spike = double.TryParse(await _settings.StringAsync("AlertSpikeFactor"), NumberStyles.Number, CultureInfo.InvariantCulture, out var f) ? f : 3;
            var findings = SecuritySignals.Findings(signals, spike);
            if (findings.Count > 0)
                await RaiseAsync("security", seen, key, "Security", "AlertSecurityTo", $"Copilot security signals on {day:ddd d MMM}",
                    [.. findings, "Who and what is on Security › Flagged interactions, for people with the Security role."], "/security/flags?d=7");
            else await SetStateAsync("security", key);
        }

        async Task AnomaliesAsync()
        {
            /* Today where the data's days are cut, not SQL Server's own date: a day still loading was judged once, and never again. */
            var end = await _db.ScalarAsync<int?>("SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < @today;", new { today = await _settings.TodayKeyAsync() });
            if (end is not int k) return;
            var day = new DateTime(k / 10000, k / 100 % 100, k % 100);
            var key = day.ToString("yyyy-MM-dd");
            var seen = await StateAsync("anomaly");
            if (seen == key) return;
            var series = await _ops.DepartmentDaysAsync(day.AddDays(-28), day, await _settings.IntAsync("MinGroupSize", 10));
            var odd = Anomalies.Find(series, day, await _settings.IntAsync("AnomalyDropPercent", 50), await _settings.IntAsync("AnomalySpikePercent", 200));
            if (odd.Count > 0)
                await RaiseAsync("anomaly", seen, key, "Anomaly", "AlertAdoptionTo", $"Copilot use changed sharply on {day:ddd d MMM} in {odd.Count} department(s)",
                    odd.Take(20).Select(a => $"{a.Unit}: {a.Value:#,0} interactions against a usual {a.Baseline:#,0} for a {day:dddd} ({a.Change:+0%;-0%}). {(a.Kind == "Drop" ? "An outage, a policy change or a holiday?" : "A launch or a training session?")}").ToList(),
                    "/adoption/anomalies");
            else await SetStateAsync("anomaly", key);
        }

        async Task AgentsAsync(bool first)
        {
            var seen = await StateAsync("agents");
            var since = DateTime.TryParse(seen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
            var latest = await _db.ScalarAsync<DateTime?>("SELECT MAX(FirstSeenUtc) FROM dash.Agent WHERE AgentSource <> N'(no agent)';");
            if (latest is null) return;
            var value = latest.Value.ToString("o", CultureInfo.InvariantCulture);
            /* The first time, remember where things stand instead of announcing every agent there is. */
            if (!first && since is not null && await _settings.StringAsync("NewAgentAlert") != "0")
            {
                var fresh = await _ops.AgentsSinceAsync(since.Value);
                if (fresh.Count > 0)
                {
                    await RaiseAsync("agents", seen, value, "NewAgent", "AlertAgentsTo", $"{fresh.Count} agent(s) used for the first time",
                        fresh.Select(a => $"{a.Name} ({a.Source}), first used {a.FirstSeenUtc:yyyy-MM-dd}.").Append("Give them a friendly name and an owner in Admin › Reference data.").ToList(), "/agents/inventory");
                    return;
                }
            }
            await SetStateAsync("agents", value);
        }

        async Task PoolAsync()
        {
            var warn = await _settings.IntAsync("PoolWarnPercent", 95);
            var full = (await _ops.PoolAsync()).Where(p => p.IsCopilot && p.Bought > 0 && 100.0 * p.Consumed / p.Bought >= warn).ToList();
            if (full.Count == 0) { await SetStateAsync("pool", null); return; }
            /* Once a week while it lasts. */
            var seen = await StateAsync("pool");
            var last = DateTime.TryParse(seen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
            if (DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
            var names = await SkuNames.LoadAsync(_db);   // Product names, not part numbers
            await RaiseAsync("pool", seen, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Pool", "AlertLicensingTo", "Copilot licenses are running out",
                full.Select(p => $"{names.Name(p.SkuPartNumber)}: {p.Consumed:#,0} of {p.Bought:#,0} assigned ({100.0 * p.Consumed / p.Bought:0}%), {p.Available:#,0} left.")
                    .Append("License optimization lists unused licenses to reclaim before buying more.").ToList(), "/licensing/pool");
        }

        /* The Copilot Credit commitment projected to run out early, or to leave part of it unused: once a week while it lasts. */
        async Task CommitmentAsync()
        {
            var days = await _settings.IntAsync("CreditCommitmentAlertDays", 30);
            var credit = new CreditChargeData(_db, _settings);
            string? warning = null;
            if (days > 0 && await credit.CommitmentsReadyAsync() && await credit.ReadyAsync() && await credit.InForceAsync() is { } c)
                warning = CreditChargeRules.Warning(await credit.StatusAsync(c), days);
            if (warning is null) { await SetStateAsync("commitment", null); return; }
            var seen = await StateAsync("commitment");
            var last = DateTime.TryParse(seen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
            if (DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
            await RaiseAsync("commitment", seen, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Commitment", "AlertLicensingTo", "The Copilot Credit commitment needs attention",
                [warning, "Forecasting › Credit commitment shows what is drawn, what is left and who draws most; it is an estimate from the consumption report."], "/forecasting/credit-commitment");
        }
        /* An AI budget set in Azure that Azure forecasts to go over (3.4.0, the whole AI bill): once a week while it lasts. */
        async Task BudgetAsync()
        {
            if (await _settings.StringAsync("AiBudgetAlert") == "0"
                || await _db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.AiBudget', N'U') IS NULL THEN 0 ELSE 1 END;") == 0)
                return;
            var over = await _db.QueryAsync<(string Budget, decimal Amount, decimal Forecast, decimal Spent, string? Currency)>(
                "SELECT TOP (10) Budget, Amount, ForecastSpend, ISNULL(CurrentSpend, 0), Currency FROM audit.AiBudget WHERE Amount > 0 AND ForecastSpend > Amount ORDER BY ForecastSpend - Amount DESC;");
            if (over.Count == 0) { await SetStateAsync("budget", null); return; }
            var seen = await StateAsync("budget");
            var last = DateTime.TryParse(seen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
            if (DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
            string M(decimal v, string? c) => $"{Math.Round(v, 0):#,0} {c ?? "USD"}";
            await RaiseAsync("budget", seen, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "Budget", "AlertLicensingTo",
                over.Count == 1 ? $"The Azure budget {over[0].Budget} is forecast to go over" : $"{over.Count} Azure budgets are forecast to go over",
                over.Select(b => $"{b.Budget}: forecast {M(b.Forecast, b.Currency)} against {M(b.Amount, b.Currency)}, {M(b.Spent, b.Currency)} spent so far.")
                    .Append("Each is a budget set in Azure at a scope the AI bill is read at, counting what its own filter says. Leadership › AI spend across Azure shows what grew fastest, service by service.").ToList(), "/leadership/ai-spend");
        }

        /* A spend limit reached, or projected to be before its period ends: once a day while it lasts, to SpendLimitAlertTo,
           and, once SpendLimitAlertToExecutives is 1, also to each such limit's L2 and L3 executives. The month-end
           snapshots are kept here too. */
        async Task SpendLimitsAsync()
        {
            var data = sp.GetRequiredService<SpendLimitData>();
            if (!await data.ReadyAsync()) return;
            await data.SyncAsync();
            var limits = await data.LimitsAsync();
            if (limits.Count == 0) return;
            var whole = await sp.GetRequiredService<OpsReports>().ContextAsync(30, null);
            var measured = await data.MeasureAsync(limits.Where(l => l.GoneUtc is null).ToList(), whole);
            await data.SnapshotAsync(measured);
            var warn = measured.Where(m => m.Warn).ToList();
            if (warn.Count == 0) { await SetStateAsync("limits", null); return; }
            var seen = await StateAsync("limits");
            var last = DateTime.TryParse(seen, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
            if (DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
            var execs = await _settings.StringAsync("SpendLimitAlertToExecutives") == "1"
                ? warn.SelectMany(m => new[] { m.Limit.L2Upn, m.Limit.L3Upn }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
            if (await _settings.StringAsync("SpendLimitAlertTo") is null && execs.Count == 0) return;
            await RaiseAsync("limits", seen, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), "SpendLimit", "SpendLimitAlertTo",
                warn.Count == 1 ? $"A spend limit will be reached: {warn[0].Limit.Name}" : $"{warn.Count} spend limits will be reached",
                warn.Take(10).Select(m => SpendLimitLine(m)).Append("Forecasting › Spend limits shows each one's spend against it, and what to set it to next quarter. The dashboard never changes a limit.").ToList(),
                "/forecasting/spend-limits", execs);
        }

        /* The agent rules, once a refresh: what they find is proposed (dash.AgentAction), and each approved Notify is
           emailed to the agent's owner (to AlertAdminTo when it has none) and marked done. Quarantines, releases and new
           owners are tools\Invoke-AgentActions.ps1's. */
        async Task AgentPoliciesAsync()
        {
            var policies = sp.GetRequiredService<AgentPolicies>();
            if (!await policies.ReadyAsync()) return;
            var made = await policies.ProposeAsync(await sp.GetRequiredService<OpsReports>().ContextAsync(30, null));
            if (made > 0) done.Add($"Agent policies: {made:#,0} new proposal(s).");
            var notify = await _db.QueryAsync<(int Id, string? Agent, string? Owner, string? Reason)>(@"
SELECT TOP (50) a.ActionId, ISNULL(a.AgentName, a.InventoryAgentId), p.Upn, a.Reason
FROM dash.AgentAction AS a
LEFT JOIN dash.AgentInventory AS i ON i.InventoryAgentId = a.InventoryAgentId
LEFT JOIN dash.Person AS p ON p.Upn = i.OwnerUpn AND p.AccountEnabled = 1
WHERE a.Status = 'Approved' AND a.ActionKind = 'Notify' AND a.DoneUtc IS NULL ORDER BY a.DecidedUtc;");
            foreach (var n in notify)
            {
                /* Not before a failed send's next try; then claimed by its row, so two web servers never both send it, and
                   given back when it is to be tried again. */
                var (tries, waiting) = await RetryAsync("agentnotify:" + n.Id.ToString(CultureInfo.InvariantCulture));
                if (waiting) continue;
                if (await _db.ExecuteAsync("UPDATE dash.AgentAction SET DoneUtc = SYSUTCDATETIME(), Result = N'Sending' WHERE ActionId = @id AND Status = 'Approved' AND DoneUtc IS NULL;", new { id = n.Id }) != 1)
                    continue;
                var sent = await SendOnceAsync("agentnotify:" + n.Id.ToString(CultureInfo.InvariantCulture), tries,
                    async () => await _db.ExecuteAsync("UPDATE dash.AgentAction SET DoneUtc = NULL, Result = NULL WHERE ActionId = @id AND Status = 'Approved';", new { id = n.Id }),
                    "AgentPolicy", n.Owner is null ? "AlertAdminTo" : "", $"Your agent {n.Agent} needs attention",
                    [$"{n.Reason} An administrator asked that its owner be told.",
                     "Look at it in Copilot Studio: keep it in use, hand it to someone, or retire it yourself. Agents › My agents shows its use."],
                    "/agents/mine", n.Owner is null ? null : [n.Owner]);
                if (sent)
                    await _db.ExecuteAsync("UPDATE dash.AgentAction SET Status = 'Done', Result = @r WHERE ActionId = @id AND Status = 'Approved';",
                        new { id = n.Id, r = $"Emailed to {n.Owner ?? "the administrators (no owner)"}." });
            }
        }

        static string SpendLimitLine(SpendLimitData.Measured m)
        {
            var p = m.Position!;
            var unit = m.Limit.Unit == "USD" ? " USD" : m.Limit.Unit == "Tokens" ? " tokens" : " credits";
            string N(decimal? v) => v is null ? "-" : Math.Round(v.Value, 0).ToString("#,0", CultureInfo.InvariantCulture) + unit;
            return p.Reached ? $"{m.Limit.Name}: reached, {N(p.Spent)} of {N(p.Limit)} this {m.Limit.Period.ToLowerInvariant()} period."
                 : p.ReachedOn is { } on ? $"{m.Limit.Name}: projected to reach {N(p.Limit)} on {on:d MMM}; {N(p.Spent)} spent so far."
                 : $"{m.Limit.Name}: {N(p.Spent)} of {N(p.Limit)} spent, past the share set to warn at.";
        }

        async Task LeaversAsync(bool first)
        {
            var n = (await _ops.LeaversAsync()).Count;
            var seen = await StateAsync("leavers");
            var before = int.TryParse(seen, out var b) ? b : 0;
            var value = n.ToString(CultureInfo.InvariantCulture);
            if (!first && n > before)
            {
                var price = decimal.TryParse(await _settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m;
                var cur = await _settings.StringAsync("LicenseCurrency") ?? "USD";
                await RaiseAsync("leavers", seen, value, "Leavers", "AlertLicensingTo", $"{n} Copilot license(s) on disabled or departed accounts",
                    [$"{n - before} more since the last check: {n:#,0} in all, {n * price:#,0} {cur} a month. The list is on Licensing › Licenses on disabled accounts, for people with the Licensing role."],
                    "/licensing/leavers");
            }
            else await SetStateAsync("leavers", value);
        }

        /// <summary>
        /// A Copilot license on a non-human account is a policy breach: each account is alerted once, to the Security and
        /// Licensing recipients, the first refresh it is found, and again if it is found after it was cleared. Its state is a row per
        /// account (nonhuman:{hash of the sign-in}), removed when the license goes or an administrator confirms a person.
        /// </summary>
        async Task NonHumanAsync()
        {
            if (await _settings.StringAsync("NonHumanAlert") == "0") return;
            var now = await NonHuman.AllAsync(_db, _settings);
            static string Key(string upn) => "nonhuman:" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(upn.ToLowerInvariant())));
            var known = (await _db.QueryAsync<string>("SELECT AlertKey FROM dash.AlertState WHERE AlertKey LIKE N'nonhuman:%';")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = now.Where(b => !known.Contains(Key(b.UserPrincipalName))).ToList();
            var (tries, waiting) = fresh.Count > 0 ? await RetryAsync("nonhuman") : ((byte)0, true);
            if (!waiting)
            {
                /* Each account claimed before the alert is sent; one another web server claimed first is in its alert, not this one. */
                var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in fresh)
                    if (await ClaimStateAsync(Key(b.UserPrincipalName), null, b.UserPrincipalName.Length > 400 ? b.UserPrincipalName[..400] : b.UserPrincipalName)) mine.Add(b.UserPrincipalName);
                var claimed = fresh.Where(b => mine.Contains(b.UserPrincipalName)).ToList();
                if (claimed.Count > 0)
                {
                    var named = !await _settings.PrivacyModeAsync();
                    var lines = new List<string> { $"{claimed.Count:#,0} account(s) that are not a person now hold a Copilot license; {now.Count:#,0} in all. The license is for people: remove it, or have an administrator confirm the account is a person." };
                    if (named) lines.AddRange(claimed.Take(25).Select(b => $"{b.UserPrincipalName}{(b.DisplayName is null ? "" : $" ({b.DisplayName})")}: {NonHuman.Why(b.Guest, b.NameLike, b.NoChart, b.TypeMatch, b.GroupMatch)}."));
                    if (named && claimed.Count > 25) lines.Add($"And {claimed.Count - 25:#,0} more.");
                    lines.Add("The list, the removal files and the confirmations are on Security, Compliance and Licensing › Copilot on non-human accounts.");
                    await SendOnceAsync("nonhuman", tries, async () => { foreach (var b in claimed) await ForgetAsync(Key(b.UserPrincipalName)); },
                        "NonHuman", "AlertSecurityTo,AlertLicensingTo", $"Policy breach: {claimed.Count:#,0} Copilot license(s) on non-human accounts", lines, CopilotInsights.Pages.Nav.NonHuman);
                }
            }
            /* Cleared (license removed, confirmed a person, rule changed): forget it, so it alerts again if it comes back. */
            var current = now.Select(b => Key(b.UserPrincipalName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var k in known.Where(k => !current.Contains(k)))
                await _db.ExecuteAsync("DELETE FROM dash.AlertState WHERE AlertKey = @k", new { k });
        }

        /* ---- Digests -------------------------------------------------------------------------------- */

        sealed class Sub
        {
            public int SubscriptionId { get; set; }
            public string Name { get; set; } = "";
            public string Report { get; set; } = "";
            public string Recipients { get; set; } = "";
            public string Frequency { get; set; } = "";
            public byte? DayOfWeek { get; set; }
            public byte? DayOfMonth { get; set; }
            public byte HourLocal { get; set; }
            public string? RootUpn { get; set; }
            public int Days { get; set; }
            public DateTime? LastSentUtc { get; set; }
            public byte FailedTries { get; set; }
            public DateTime? RetryAfterUtc { get; set; }
        }

        public async Task DigestsAsync()
        {
            var subs = await _db.QueryAsync<Sub>("SELECT SubscriptionId, Name, Report, Recipients, Frequency, DayOfWeek, DayOfMonth, HourLocal, RootUpn, Days, LastSentUtc, FailedTries, RetryAfterUtc FROM dash.Subscription WHERE Enabled = 1;");
            if (subs.Count == 0) return;
            var zone = await _ops.ZoneAsync();
            foreach (var s in subs.Where(s => Schedule.IsDue(s.Frequency, s.DayOfWeek, s.DayOfMonth, s.HourLocal, s.LastSentUtc, DateTime.UtcNow, zone)
                                              && Retry.Ready(s.RetryAfterUtc, DateTime.UtcNow)))
            {
                /* Claimed before it is sent: marked sent now, and only if nobody has sent it since this round read it.
                   Before, it was marked after the email had gone, so a database that could be read but not written (a full
                   log) mailed the same digest every five minutes, and two servers whose lock had slipped both mailed it. */
                if (await _db.ExecuteAsync(@"UPDATE dash.Subscription SET LastSentUtc = SYSUTCDATETIME(), LastStatus = N'Being sent. If this stays, the site stopped or could not record the result; it is not sent again.'
WHERE SubscriptionId = @id AND Enabled = 1 AND (LastSentUtc = @seen OR (LastSentUtc IS NULL AND @seen IS NULL))
  AND (RetryAfterUtc IS NULL OR RetryAfterUtc <= SYSUTCDATETIME());", new { id = s.SubscriptionId, seen = s.LastSentUtc }) != 1)
                    continue;
                string status;
                bool sent;
                try
                {
                    var (subject, html, text) = await _reports.DigestAsync(s.Report, s.Days, s.RootUpn);
                    var error = await _mail.SendAsync("Digest", MailConfig.Recipients(s.Recipients), subject, html, text, ct);
                    sent = error is null;
                    status = sent ? "Sent" : "Not sent: " + error;
                }
                /* Not to be sent as it is set up: skipped until it is next due, not retried. */
                catch (DigestRefused ex) { sent = true; status = "Not sent: " + ex.Message + "."; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    /* Stopped before the email went (Mailer sends nothing it was stopped during): due again at the next start. */
                    await _db.ExecuteAsync("UPDATE dash.Subscription SET LastSentUtc = @seen, LastStatus = N'The site stopped before it was sent; it goes when the site starts again.' WHERE SubscriptionId = @id;",
                        new { id = s.SubscriptionId, seen = s.LastSentUtc });
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { sent = false; status = Incident.Note(_log, ex, "Failed"); }
                var next = Retry.After(sent, s.FailedTries, DateTime.UtcNow);
                if (next.Retry) status += $" Trying again after {next.RetryAfterUtc:HH:mm} UTC (try {s.FailedTries + 2} of {Retry.Tries}).";
                /* A try to come puts back when it was last sent; anything else keeps the claim's time. */
                await _db.ExecuteAsync(@"UPDATE dash.Subscription SET LastSentUtc = CASE WHEN @retry = 1 THEN @seen ELSE LastSentUtc END,
    FailedTries = @tries, RetryAfterUtc = @after, LastStatus = @status WHERE SubscriptionId = @id;",
                    new { id = s.SubscriptionId, seen = s.LastSentUtc, retry = next.Retry, tries = next.FailedTries, after = next.RetryAfterUtc, status = status.Length > 400 ? status[..400] : status });
                done.Add($"Digest \"{s.Name}\": {status}");
            }
        }
    }
}
