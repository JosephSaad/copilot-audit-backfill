using System.Globalization;
using CopilotInsights.Data;
using Dapper;
using Microsoft.Extensions.Options;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The dashboard's background sender. Every few minutes, on one web server at a time (a SQL
/// application lock), it checks the pipeline and the data, raises alerts, and sends the digests
/// that are due. Alerts go by email to the recipients set in Admin > Alerts and digests, and to
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
               here stopped the whole site (2.4.12): a mail server or token endpoint that did not answer took it down. */
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

    /// <summary>One round of checks in the current tenant's database. Returns what it did, for Admin > Alerts and digests' "Run the checks now".</summary>
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
            await run.PipelineAsync();
            await run.FreshnessAsync();
            await run.AfterRefreshAsync();
            await run.DigestsAsync();
            await run.CertificateAsync();
            await run.ManagerDigestsAsync();
            await run.ExtractsAsync();
        }
        finally { await conn.ExecuteAsync("EXEC sp_releaseapplock @Resource = N'CopilotInsightsNotifier', @LockOwner = 'Session';"); }
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

        /// <summary>Emails the alert to the setting's recipients and writes it to the Windows Application log.</summary>
        async Task AlertAsync(string kind, string recipientsSetting, string subject, IReadOnlyList<string> lines, string? link = null)
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
            var error = await _mail.SendAsync(kind, MailConfig.Recipients(string.Join(";", to)), subject, email.Html(), email.Text(), ct);
            if (await _settings.StringAsync("EventLogAlerts") != "0") WriteEventLog(kind, subject + Environment.NewLine + string.Join(Environment.NewLine, lines));
            await WebhookAsync(kind, subject, lines, url is not null && link is not null ? url.TrimEnd('/') + link : null);
            done.Add(error is null ? $"Alert sent: {subject}" : $"Alert recorded, not emailed ({error}): {subject}");
        }

        void WriteEventLog(string kind, string message)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                var id = kind switch { "Pipeline" => 1001, "Freshness" => 1002, "Certificate" => 1003, "Security" => 2001, "Anomaly" => 3001, "Pool" => 4001, "Leavers" => 4002, "NonHuman" => 2002, "NewAgent" => 5001, _ => 1000 };
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
            var seen = int.TryParse(await StateAsync("pipeline"), out var s) ? s : 0;
            foreach (var r in runs.Where(r => r.RunId > seen && r.Status == "Failed").OrderBy(r => r.RunId))
                await AlertAsync("Pipeline", "AlertAdminTo", $"Copilot pipeline failed: {r.FailedStep}",
                    [$"The {r.Kind.ToLowerInvariant()} run that started {Zones.Stamp(r.StartedUtc, await _ops.ZoneAsync())} on {r.Host} stopped at \"{r.FailedStep}\". The next run resumes it; to resume now, run Invoke-CopilotPipeline.ps1 on the job server.",
                     r.Detail is null ? "" : "Its last lines:\n" + r.Detail], "/admin/health");
            var stuck = runs.FirstOrDefault(r => r.Status == "Running" && r.StartedUtc < DateTime.UtcNow.AddHours(-12));
            if (stuck is not null && await StateAsync("pipeline-stuck") != stuck.RunId.ToString(CultureInfo.InvariantCulture))
            {
                await AlertAsync("Pipeline", "AlertAdminTo", "Copilot pipeline has not finished in 12 hours",
                    [$"The {stuck.Kind.ToLowerInvariant()} run that started {Zones.Stamp(stuck.StartedUtc, await _ops.ZoneAsync())} on {stuck.Host} is still marked as running. A reboot or a killed task leaves it like this; check the log on the job server."], "/admin/health");
                await SetStateAsync("pipeline-stuck", stuck.RunId.ToString(CultureInfo.InvariantCulture));
            }
            var max = runs.Where(r => r.Status != "Running").Select(r => r.RunId).DefaultIfEmpty(seen).Max();
            if (max > seen) await SetStateAsync("pipeline", max.ToString(CultureInfo.InvariantCulture));
        }

        public async Task FreshnessAsync()
        {
            var (last, age, limit) = await _ops.FreshnessAsync();
            if (last is null || age <= limit) return;
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (await StateAsync("freshness") == today) return;
            await AlertAsync("Freshness", "AlertAdminTo", $"Copilot Insights data is {age:0} hours old",
                [$"The dashboard's data was last refreshed {Zones.Stamp(last, await _ops.ZoneAsync())}, more than {limit} hours ago. The nightly task on the job server may not be running: check Task Scheduler and Admin > Health."], "/admin/health");
            await SetStateAsync("freshness", today);
        }

        /* ---- After each refresh: security, drops and spikes, agents, the pool, leavers ------------ */

        public async Task AfterRefreshAsync()
        {
            var runId = await _db.ScalarAsync<int?>("SELECT MAX(RunId) FROM dash.RefreshRun;");
            if (runId is null) return;
            var last = await StateAsync("refresh");
            if (last == runId.Value.ToString(CultureInfo.InvariantCulture)) return;
            var first = last is null;

            await SecurityAsync();
            await AnomaliesAsync();
            await AgentsAsync(first);
            await PoolAsync();
            await LeaversAsync(first);
            await NonHumanAsync();
            await SetStateAsync("refresh", runId.Value.ToString(CultureInfo.InvariantCulture));
        }

        async Task SecurityAsync()
        {
            var (day, signals) = await _ops.SecurityDayAsync();
            if (day is null) return;
            var key = day.Value.ToString("yyyy-MM-dd");
            if (await StateAsync("security") == key) return;
            var spike = double.TryParse(await _settings.StringAsync("AlertSpikeFactor"), NumberStyles.Number, CultureInfo.InvariantCulture, out var f) ? f : 3;
            var findings = SecuritySignals.Findings(signals, spike);
            if (findings.Count > 0)
                await AlertAsync("Security", "AlertSecurityTo", $"Copilot security signals on {day:ddd d MMM}",
                    [.. findings, "Who and what is on Security > Flagged interactions, for people with the Security role."], "/security/flags?d=7");
            await SetStateAsync("security", key);
        }

        async Task AnomaliesAsync()
        {
            var end = await _db.ScalarAsync<int?>("SELECT MAX(DateKey) FROM dash.UserDay WHERE DateKey < YEAR(SYSDATETIME()) * 10000 + MONTH(SYSDATETIME()) * 100 + DAY(SYSDATETIME());");
            if (end is not int k) return;
            var day = new DateTime(k / 10000, k / 100 % 100, k % 100);
            if (await StateAsync("anomaly") == day.ToString("yyyy-MM-dd")) return;
            var series = await _ops.DepartmentDaysAsync(day.AddDays(-28), day, await _settings.IntAsync("MinGroupSize", 5));
            var odd = Anomalies.Find(series, day, await _settings.IntAsync("AnomalyDropPercent", 50), await _settings.IntAsync("AnomalySpikePercent", 200));
            if (odd.Count > 0)
                await AlertAsync("Anomaly", "AlertAdoptionTo", $"Copilot use changed sharply on {day:ddd d MMM} in {odd.Count} department(s)",
                    odd.Take(20).Select(a => $"{a.Unit}: {a.Value:#,0} interactions against a usual {a.Baseline:#,0} for a {day:dddd} ({a.Change:+0%;-0%}). {(a.Kind == "Drop" ? "An outage, a policy change or a holiday?" : "A launch or a training session?")}").ToList(),
                    "/adoption/anomalies");
            await SetStateAsync("anomaly", day.ToString("yyyy-MM-dd"));
        }

        async Task AgentsAsync(bool first)
        {
            var since = DateTime.TryParse(await StateAsync("agents"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
            var latest = await _db.ScalarAsync<DateTime?>("SELECT MAX(FirstSeenUtc) FROM dash.Agent WHERE AgentSource <> N'(no agent)';");
            if (latest is null) return;
            /* The first time, remember where things stand instead of announcing every agent there is. */
            if (!first && since is not null && await _settings.StringAsync("NewAgentAlert") != "0")
            {
                var fresh = await _ops.AgentsSinceAsync(since.Value);
                if (fresh.Count > 0)
                    await AlertAsync("NewAgent", "AlertAgentsTo", $"{fresh.Count} agent(s) used for the first time",
                        fresh.Select(a => $"{a.Name} ({a.Source}), first used {a.FirstSeenUtc:yyyy-MM-dd}.").Append("Give them a friendly name and an owner in Admin > Reference data.").ToList(), "/agents/inventory");
            }
            await SetStateAsync("agents", latest.Value.ToString("o", CultureInfo.InvariantCulture));
        }

        async Task PoolAsync()
        {
            var warn = await _settings.IntAsync("PoolWarnPercent", 95);
            var full = (await _ops.PoolAsync()).Where(p => p.IsCopilot && p.Bought > 0 && 100.0 * p.Consumed / p.Bought >= warn).ToList();
            if (full.Count == 0) { await SetStateAsync("pool", null); return; }
            /* Once a week while it lasts. */
            var last = DateTime.TryParse(await StateAsync("pool"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
            if (DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
            await AlertAsync("Pool", "AlertLicensingTo", "Copilot licenses are running out",
                full.Select(p => $"{p.SkuPartNumber}: {p.Consumed:#,0} of {p.Bought:#,0} assigned ({100.0 * p.Consumed / p.Bought:0}%), {p.Available:#,0} left.")
                    .Append("License optimization lists unused licenses to reclaim before buying more.").ToList(), "/licensing/pool");
            await SetStateAsync("pool", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        async Task LeaversAsync(bool first)
        {
            var n = (await _ops.LeaversAsync()).Count;
            var before = int.TryParse(await StateAsync("leavers"), out var b) ? b : 0;
            if (!first && n > before)
            {
                var price = decimal.TryParse(await _settings.StringAsync("LicenseMonthlyCost"), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 30m;
                var cur = await _settings.StringAsync("LicenseCurrency") ?? "USD";
                await AlertAsync("Leavers", "AlertLicensingTo", $"{n} Copilot license(s) on disabled or departed accounts",
                    [$"{n - before} more since the last check: {n:#,0} in all, {n * price:#,0} {cur} a month. The list is on Licensing > Licenses on disabled accounts, for people with the Licensing role."],
                    "/licensing/leavers");
            }
            await SetStateAsync("leavers", n.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A Copilot license on a non-human account is a policy breach (2.4.5): each account is alerted once, to the Security and
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
            if (fresh.Count > 0)
            {
                var named = !await _settings.PrivacyModeAsync();
                var lines = new List<string> { $"{fresh.Count:#,0} account(s) that are not a person now hold a Copilot license; {now.Count:#,0} in all. The license is for people: remove it, or have an administrator confirm the account is a person." };
                if (named) lines.AddRange(fresh.Take(25).Select(b => $"{b.UserPrincipalName}{(b.DisplayName is null ? "" : $" ({b.DisplayName})")}: {NonHuman.Why(b.Guest, b.NameLike, b.NoChart, b.TypeMatch, b.GroupMatch)}."));
                if (named && fresh.Count > 25) lines.Add($"And {fresh.Count - 25:#,0} more.");
                lines.Add("The list, the removal files and the confirmations are on Security, Compliance and Licensing > Copilot on non-human accounts.");
                await AlertAsync("NonHuman", "AlertSecurityTo,AlertLicensingTo", $"Policy breach: {fresh.Count:#,0} Copilot license(s) on non-human accounts", lines, "/security/non-human");
                foreach (var b in fresh) await SetStateAsync(Key(b.UserPrincipalName), b.UserPrincipalName.Length > 400 ? b.UserPrincipalName[..400] : b.UserPrincipalName);
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
                string status;
                bool sent;
                try
                {
                    var (subject, html, text) = await _reports.DigestAsync(s.Report, s.Days, s.RootUpn);
                    var error = await _mail.SendAsync("Digest", MailConfig.Recipients(s.Recipients), subject, html, text, ct);
                    sent = error is null;
                    status = sent ? "Sent" : "Not sent: " + error;
                }
                /* Not to be sent as it is set up (2.4.12): skipped until it is next due, not retried. */
                catch (DigestRefused ex) { sent = true; status = "Not sent: " + ex.Message + "."; }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { sent = false; status = "Failed: " + ex.Message; }
                var next = Retry.After(sent, s.FailedTries, DateTime.UtcNow);
                if (next.Retry) status += $" Trying again after {next.RetryAfterUtc:HH:mm} UTC (try {s.FailedTries + 2} of {Retry.Tries}).";
                await _db.ExecuteAsync(@"UPDATE dash.Subscription SET LastSentUtc = CASE WHEN @retry = 1 THEN LastSentUtc ELSE SYSUTCDATETIME() END,
    FailedTries = @tries, RetryAfterUtc = @after, LastStatus = @status WHERE SubscriptionId = @id;",
                    new { id = s.SubscriptionId, retry = next.Retry, tries = next.FailedTries, after = next.RetryAfterUtc, status = status.Length > 400 ? status[..400] : status });
                done.Add($"Digest \"{s.Name}\": {status}");
            }
        }
    }
}
