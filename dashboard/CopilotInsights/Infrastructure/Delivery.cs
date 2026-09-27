using System.Globalization;
using System.Text;
using System.Text.Json;
using CopilotInsights.Data;
using Dapper;

namespace CopilotInsights.Infrastructure;

/// <summary>Alert payloads for a webhook: a Teams Workflows / incoming-webhook card, or plain JSON for ServiceNow and the rest. Pure, for the tests.</summary>
public static class Webhook
{
    public static readonly string[] Formats = ["Teams", "Json"];

    public static string Payload(string format, string kind, string subject, IReadOnlyList<string> lines, string? link, DateTime atUtc)
    {
        var text = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (format == "Json")
            return JsonSerializer.Serialize(new { source = "Copilot Insights", kind, subject, lines = text, link, atUtc = atUtc.ToString("o", CultureInfo.InvariantCulture) });
        var body = new List<object> { new { type = "TextBlock", text = subject, weight = "Bolder", size = "Medium", wrap = true } };
        body.AddRange(text.Take(20).Select(l => (object)new { type = "TextBlock", text = l.Length > 1500 ? l[..1500] + "…" : l, wrap = true }));
        var card = new Dictionary<string, object>
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4", ["body"] = body,
        };
        if (link is not null) card["actions"] = new[] { new { type = "Action.OpenUrl", title = "Open in Copilot Insights", url = link } };
        return JsonSerializer.Serialize(new { type = "message", attachments = new[] { new { contentType = "application/vnd.microsoft.card.adaptive", contentUrl = (string?)null, content = card } } });
    }

    /// <summary>Only https, and never a link-local or loopback address (the site's own metadata endpoints).</summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "the webhook address starts with https://.";
        if (u.IsLoopback || u.Host.StartsWith("169.254.", StringComparison.Ordinal)) return "the webhook address cannot be this server or a link-local address.";
        return null;
    }
}

/// <summary>When to warn that the pipeline's app certificate is expiring. Pure, for the tests.</summary>
public static class CertificateWarning
{
    /// <summary>Which warning is due: the configured days, 14, 3, then expired. Null when none is.</summary>
    public static string? Step(double daysLeft, int warnDays) =>
        daysLeft <= 0 ? "expired" : daysLeft <= 3 ? "3" : daysLeft <= 14 ? "14" : daysLeft <= warnDays ? warnDays.ToString(CultureInfo.InvariantCulture) : null;
}

/// <summary>The datasets a scheduled extract may write: aggregates only, never a named person.</summary>
public static class ExtractData
{
    public static readonly Dictionary<string, (string Label, string Sql)> Datasets = new()
    {
        ["DepartmentWeek"] = ("Use by department and week (groups under the minimum size hidden)",
            "SELECT WeekEnding, Department, ActivePeople, Interactions, Sessions FROM bi.DepartmentWeek WHERE Hidden = 0 AND WeekEnding >= @d0 ORDER BY WeekEnding, Department;"),
        ["DailyTotals"] = ("People using Copilot and interactions, per day",
            @"SELECT d.[Date], COUNT(DISTINCT CASE WHEN ud.Interactions > 0 THEN ud.UserKey END) AS ActivePeople, SUM(CAST(ud.Interactions AS BIGINT)) AS Interactions,
       SUM(CAST(ud.AgentInteractions AS BIGINT)) AS AgentInteractions, SUM(CAST(ud.WebGrounded AS BIGINT)) AS WebGrounded
FROM dash.UserDay AS ud JOIN bi.DimDate AS d ON d.DateKey = ud.DateKey WHERE d.[Date] >= @d0 GROUP BY d.[Date] ORDER BY d.[Date];"),
        ["AppWeek"] = ("Interactions and people by app and week",
            @"SELECT d.WeekEnding, a.App, COUNT_BIG(*) AS Interactions, COUNT(DISTINCT f.UserKey) AS People
FROM bi.FactInteraction AS f JOIN bi.DimApp AS a ON a.AppKey = f.AppKey JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE d.[Date] >= @d0 GROUP BY d.WeekEnding, a.App ORDER BY d.WeekEnding, a.App;"),
        ["LicensePool"] = ("Licenses bought and assigned, per day",
            "SELECT SnapshotDate, SkuPartNumber, IsCopilot, Enabled, Warning, Suspended, Consumed FROM audit.LicenseSkuDay WHERE SnapshotDate >= @d0 ORDER BY SnapshotDate, SkuPartNumber;"),
        ["AgentWeek"] = ("Agent use by week",
            @"SELECT d.WeekEnding, ag.AgentName, ag.AgentSource, COUNT_BIG(*) AS Interactions, COUNT(DISTINCT f.UserKey) AS People
FROM bi.FactInteraction AS f JOIN dash.Agent AS ag ON ag.AgentKey = f.AgentKey AND ag.AgentSource <> N'(no agent)' JOIN bi.DimDate AS d ON d.DateKey = f.DateKey
WHERE d.[Date] >= @d0 GROUP BY d.WeekEnding, ag.AgentName, ag.AgentSource HAVING COUNT(DISTINCT f.UserKey) >= @minGroup ORDER BY d.WeekEnding, ag.AgentName;"),
    };

    /// <summary>Rows as CSV, with a header, formulas neutralised.</summary>
    public static string ToCsv(IEnumerable<IDictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var r in rows)
        {
            if (first) { sb.AppendLine(string.Join(",", r.Keys.Select(Q))); first = false; }
            sb.AppendLine(string.Join(",", r.Values.Select(v => Q(v switch
            {
                null => "", DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
                bool b => b ? "1" : "0",
                _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
            }))));
        }
        return sb.ToString();
    }

    static string Q(string s)
    {
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>A folder the extracts may write to: a local path or a UNC share, never a device or a relative path.</summary>
    public static string? FolderProblem(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "give a folder.";
        var f = folder.Trim();
        if (f.Length > 400) return "the folder path is too long.";
        if (!(f.StartsWith(@"\\", StringComparison.Ordinal) || (f.Length >= 3 && char.IsLetter(f[0]) && f[1] == ':' && f[2] == '\\'))) return @"the folder is a full path such as D:\Extracts or \\server\share\copilot.";
        if (f.Contains("..", StringComparison.Ordinal) || f.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "the folder path is not valid.";
        return null;
    }

    /// <summary>A file name from the extract's name: letters, digits, dash and underscore.</summary>
    public static string FileName(string name, DateTime day) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-') + $"-{day:yyyyMMdd}.csv";
}

public sealed partial class Notifier
{
    sealed partial class Run
    {
        /* ---- Webhook -------------------------------------------------------------------------- */

        async Task WebhookAsync(string kind, string subject, IReadOnlyList<string> lines, string? link)
        {
            var url = await sp.GetRequiredService<SecretStore>().GetAsync(SecretStore.AlertWebhookUrl);
            if (url is null || Webhook.Problem(url) is not null) return;
            var format = await _settings.StringAsync("AlertWebhookFormat") is "Json" ? "Json" : "Teams";
            string status = "Sent";
            string? error = null;
            try
            {
                using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                using var body = new StringContent(Webhook.Payload(format, kind, subject, lines, link, DateTime.UtcNow), Encoding.UTF8, "application/json");
                using var r = await http.PostAsync(url, body, ct);
                if (!r.IsSuccessStatusCode) { status = "Failed"; error = $"HTTP {(int)r.StatusCode} {r.ReasonPhrase}"; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { status = "Failed"; error = ex.Message; }
            await _db.ExecuteAsync("INSERT INTO dash.NotificationLog (Kind, Subject, Recipients, Status, Error) VALUES (@kind, @subject, N'webhook', @status, @error);",
                new { kind = "Webhook", subject = subject.Length > 400 ? subject[..400] : subject, status, error = error is { Length: > 1000 } ? error[..1000] : error });
            if (error is not null) done.Add($"Webhook not delivered ({error}): {subject}");
        }

        /* ---- The app certificate ----------------------------------------------------------------- */

        public async Task CertificateAsync()
        {
            if (await _db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'audit.PipelineRun', N'CertificateExpiresUtc') IS NULL THEN 0 ELSE 1 END") == 0) return;
            var cert = (await _db.QueryAsync<(string? Thumbprint, DateTime? ExpiresUtc, string? Subject)>(
                "SELECT TOP (1) CertificateThumbprint, CertificateExpiresUtc, CertificateSubject FROM audit.PipelineRun WHERE CertificateExpiresUtc IS NOT NULL ORDER BY RunId DESC;")).FirstOrDefault();
            if (cert.ExpiresUtc is null) return;
            var left = (cert.ExpiresUtc.Value - DateTime.UtcNow).TotalDays;
            var warn = await _settings.IntAsync("CertificateWarnDays", 30);
            var step = CertificateWarning.Step(left, warn);
            if (step is null) return;
            var key = $"{cert.Thumbprint}|{step}";
            if (await StateAsync("certificate") == key) return;
            var subject = left <= 0 ? "The Copilot pipeline's certificate has expired" : $"The Copilot pipeline's certificate expires in {Math.Ceiling(left):0} day(s)";
            await AlertAsync("Certificate", "AlertAdminTo", subject,
                [$"The app certificate {cert.Subject} ({cert.Thumbprint}) that the job server signs in to Microsoft Graph with expires {cert.ExpiresUtc:yyyy-MM-dd HH:mm} UTC. When it does, the nightly load stops.",
                 "Renew it: run tools\\Install-CopilotJobServer.ps1 -RegisterApp on the job server as an administrator (a new certificate, uploaded to the app, its thumbprint in the settings). The operations guide has the steps."], "/admin");
            await SetStateAsync("certificate", key);
        }

        /* ---- Manager and personal digests --------------------------------------------------------- */

        const int DigestsPerRound = 200;

        /// <summary>The digest round due now: its period key (the local date it fell due), if it fell due in the last three days.</summary>
        async Task<(string Period, string Frequency)?> DigestRoundAsync(string frequency)
        {
            var zone = await _ops.ZoneAsync();
            var day = await _settings.IntAsync("ManagerDigestDay", 1);
            if (await _settings.StringAsync("ManagerDigestDay") == "0") day = 0;
            var hour = await _settings.StringAsync("DigestHour") is { } h && int.TryParse(h, out var hh) && hh is >= 0 and <= 23 ? hh : 7;
            var due = Schedule.LastDueUtc(frequency, day, 1, hour, DateTime.UtcNow, zone);
            if (due is null || DateTime.UtcNow - due.Value > TimeSpan.FromDays(3)) return null;
            return (TimeZoneInfo.ConvertTimeFromUtc(due.Value, zone).ToString("yyyy-MM-dd"), frequency);
        }

        /// <summary>
        /// A send is due when there is none for the person and period yet, or when an earlier try failed (Fail1, Fail2)
        /// at least an hour ago: three tries in all, so a mail server that is down for a while does not cost a week's digest.
        /// </summary>
        internal const string Due = @"NOT EXISTS (SELECT 1 FROM dash.DigestSend AS s WHERE s.Upn = {0} AND s.Kind = @kind AND s.PeriodKey = @period
                   AND NOT (s.Status IN ('Fail1', 'Fail2') AND s.SentUtc < DATEADD(HOUR, -1, SYSUTCDATETIME())))";

        /// <summary>Claims a send for a person and period; false when it was sent, is being sent, or has failed three times.</summary>
        async Task<bool> ClaimAsync(string upn, string kind, string period) =>
            await _db.ExecuteAsync(@"
UPDATE dash.DigestSend SET Status = CASE Status WHEN 'Fail1' THEN 'Sending2' ELSE 'Sending3' END, SentUtc = SYSUTCDATETIME()
WHERE Upn = @upn AND Kind = @kind AND PeriodKey = @period AND Status IN ('Fail1', 'Fail2') AND SentUtc < DATEADD(HOUR, -1, SYSUTCDATETIME());
IF @@ROWCOUNT = 0
    INSERT INTO dash.DigestSend (Upn, Kind, PeriodKey, Status)
    SELECT @upn, @kind, @period, 'Sending1' WHERE NOT EXISTS (SELECT 1 FROM dash.DigestSend WHERE Upn = @upn AND Kind = @kind AND PeriodKey = @period);",
                new { upn, kind, period }) == 1;

        /// <summary>Sent, or the try's failure (Sending2 becomes Fail2; Fail3 is final).</summary>
        Task MarkAsync(string upn, string kind, string period, bool sent) =>
            _db.ExecuteAsync(@"UPDATE dash.DigestSend SET Status = CASE WHEN @sent = 1 THEN 'Sent' ELSE REPLACE(Status, 'Sending', 'Fail') END, SentUtc = SYSUTCDATETIME()
WHERE Upn = @upn AND Kind = @kind AND PeriodKey = @period;", new { upn, kind, period, sent });

        public async Task ManagerDigestsAsync()
        {
            var freq = await _settings.StringAsync("ManagerDigest");
            if (freq is not ("Weekly" or "Monthly") || !await _ops.HasAsync("dash.DigestSend")) return;
            if (await _settings.PrivacyModeAsync()) return;   // unit names are people's names
            if (await DigestRoundAsync(freq) is not { } round) return;
            var min = await _settings.IntAsync("ManagerDigestMinPeople", 10);
            var managers = await _db.QueryAsync<(string Upn, string? Mail)>(@"
SELECT TOP (@take) p.Upn, NULLIF(ud.Mail, N'')
FROM dash.Person AS p
LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = p.Upn
LEFT JOIN dash.DigestPreference AS pref ON pref.Upn = p.Upn
WHERE p.AccountEnabled = 1 AND p.PeopleBelow >= @min AND ISNULL(pref.ManagerDigestOptOut, 0) = 0
  AND " + string.Format(Due, "p.Upn") + @"
ORDER BY p.PeopleBelow DESC;", new { take = DigestsPerRound, min, period = round.Period, kind = "Manager" });
            int sent = 0, failed = 0;
            foreach (var m in managers)
            {
                if (!await ClaimAsync(m.Upn, "Manager", round.Period)) continue;
                string status;
                try
                {
                    var (subject, html, text) = await _reports.DigestAsync("Leadership", freq == "Weekly" ? 7 : 30, m.Upn);
                    var error = await _mail.SendAsync("ManagerDigest", MailConfig.Recipients(m.Mail ?? m.Upn), subject, html, text, ct);
                    status = error is null ? "Sent" : "NotSent";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Manager digest for {Upn}", m.Upn); status = "Failed"; }
                await MarkAsync(m.Upn, "Manager", round.Period, status == "Sent");
                if (status == "Sent") sent++; else failed++;
            }
            if (sent + failed > 0) done.Add($"Manager digests for {round.Period}: {sent} sent" + (failed > 0 ? $", {failed} not sent (see Admin > Email)" : "") + ".");
        }

        public async Task PersonalDigestsAsync()
        {
            if (await _settings.StringAsync("PersonalDigest") == "Off" || !await _ops.HasAsync("dash.DigestPreference")) return;
            if (await DigestRoundAsync("Weekly") is not { } round) return;
            var people = await _db.QueryAsync<(string Upn, string? Mail)>(@"
SELECT TOP (@take) pref.Upn, NULLIF(ud.Mail, N'')
FROM dash.DigestPreference AS pref
LEFT JOIN audit.UserDirectory AS ud ON ud.UserPrincipalName = pref.Upn
WHERE pref.PersonalDigest = 1
  AND " + string.Format(Due, "pref.Upn") + ";",
                new { take = DigestsPerRound, period = round.Period, kind = "Personal" });
            int sent = 0;
            foreach (var p in people)
            {
                if (!await ClaimAsync(p.Upn, "Personal", round.Period)) continue;
                string status;
                try
                {
                    var (subject, html, text) = await _reports.PersonalDigestAsync(p.Upn);
                    status = await _mail.SendAsync("PersonalDigest", MailConfig.Recipients(p.Mail ?? p.Upn), subject, html, text, ct) is null ? "Sent" : "NotSent";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Personal digest for {Upn}", p.Upn); status = "Failed"; }
                await MarkAsync(p.Upn, "Personal", round.Period, status == "Sent");
                if (status == "Sent") sent++;
            }
            if (people.Count > 0) done.Add($"Personal digests for {round.Period}: {sent} of {people.Count} sent.");
        }

        /* ---- Scheduled extracts ------------------------------------------------------------------ */

        sealed class ExtractRow
        {
            public int ExtractId { get; set; }
            public string Name { get; set; } = "";
            public string Dataset { get; set; } = "";
            public string Folder { get; set; } = "";
            public string Frequency { get; set; } = "";
            public byte? DayOfWeek { get; set; }
            public byte? DayOfMonth { get; set; }
            public byte HourLocal { get; set; }
            public int Days { get; set; }
            public DateTime? LastRunUtc { get; set; }
        }

        public async Task ExtractsAsync()
        {
            if (!await _ops.HasAsync("dash.Extract")) return;
            var rows = await _db.QueryAsync<ExtractRow>("SELECT ExtractId, Name, Dataset, Folder, Frequency, DayOfWeek, DayOfMonth, HourLocal, Days, LastRunUtc FROM dash.Extract WHERE Enabled = 1;");
            if (rows.Count == 0) return;
            var zone = await _ops.ZoneAsync();
            foreach (var x in rows.Where(x => Schedule.IsDue(x.Frequency, x.DayOfWeek, x.DayOfMonth, x.HourLocal, x.LastRunUtc, DateTime.UtcNow, zone)))
                done.Add($"Extract \"{x.Name}\": " + await WriteExtractAsync(x.ExtractId));
        }

        /// <summary>Writes one extract now and records the result. Also used by Admin > Scheduled extracts' Run now.</summary>
        public async Task<string> WriteExtractAsync(int id)
        {
            var x = (await _db.QueryAsync<ExtractRow>("SELECT ExtractId, Name, Dataset, Folder, Frequency, DayOfWeek, DayOfMonth, HourLocal, Days, LastRunUtc FROM dash.Extract WHERE ExtractId = @id;", new { id })).FirstOrDefault();
            if (x is null) return "no such extract.";
            string status;
            try
            {
                if (!ExtractData.Datasets.TryGetValue(x.Dataset, out var ds)) throw new InvalidOperationException($"unknown dataset {x.Dataset}.");
                if (ExtractData.FolderProblem(x.Folder) is { } bad) throw new InvalidOperationException(bad);
                var privacy = await _settings.PrivacyModeAsync();
                await using var conn = _db.Open();
                var data = (await conn.QueryAsync(new CommandDefinition(ds.Sql, new { d0 = DateTime.UtcNow.Date.AddDays(-x.Days), minGroup = await _settings.IntAsync("MinGroupSize", 5) }, commandTimeout: 600, cancellationToken: ct)))
                    .Cast<IDictionary<string, object?>>().ToList();
                _ = privacy;   // every dataset is an aggregate, so privacy mode changes nothing
                var file = Path.Combine(x.Folder.Trim(), ExtractData.FileName(x.Name, DateTime.UtcNow));
                await File.WriteAllTextAsync(file, ExtractData.ToCsv(data), new UTF8Encoding(true), ct);
                status = $"{data.Count:#,0} rows to {file}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { status = "Failed: " + ex.Message; }
            await _db.ExecuteAsync("UPDATE dash.Extract SET LastRunUtc = SYSUTCDATETIME(), LastStatus = LEFT(@status, 400) WHERE ExtractId = @id;", new { id, status });
            return status;
        }
    }

    /// <summary>One extract, now, in the current tenant (Admin > Scheduled extracts).</summary>
    public async Task<string> WriteExtractNowAsync(int id, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        return await new Run(scope.ServiceProvider, [], ct).WriteExtractAsync(id);
    }
}
