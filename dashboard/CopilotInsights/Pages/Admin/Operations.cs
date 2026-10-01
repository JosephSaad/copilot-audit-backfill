using System.Globalization;
using System.Net;
using System.Text;
using CopilotInsights.Data;
using CopilotInsights.Infrastructure;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;

namespace CopilotInsights.Pages.Admin;

/// <summary>Saving dash.Setting rows from the admin forms: only what changed, with who and when.</summary>
static class SettingWriter
{
    public static async Task<List<string>> SaveAsync(Db db, Settings settings, string by, IReadOnlyDictionary<string, string?> values)
    {
        var before = await settings.AllAsync();
        var changed = new List<string>();
        foreach (var (name, raw) in values)
        {
            var v = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (before.GetValueOrDefault(name) == v) continue;
            await db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @n;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (@n, @v, @by);", new { n = name, v, by });
            changed.Add(name);
        }
        settings.Invalidate();
        return changed;
    }

    public static string Saved(List<string> changed) => changed.Count == 0 ? "Nothing changed." : "Saved: " + string.Join(", ", changed) + ".";

    public static int Int(string? v, string what, int min, int max)
    {
        if (!int.TryParse(v, out var i) || i < min || i > max) throw new ArgumentException($"{what} is a whole number from {min} to {max}.");
        return i;
    }

    public static string? Emails(string? v, string what)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var parts = v.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var good = MailConfig.Recipients(v);
        if (good.Count != parts.Length)
        {
            var bad = parts.FirstOrDefault(p => !good.Any(g => p.Contains(g.Address, StringComparison.OrdinalIgnoreCase))) ?? parts[0];
            throw new ArgumentException($"{what}: '{bad}' is not an email address.");
        }
        var joined = string.Join("; ", good.Select(g => g.Address));
        if (joined.Length > 400) throw new ArgumentException($"{what}: too many addresses (400 characters at most); use a distribution list.");
        return joined;
    }
}

/* ---- Email --------------------------------------------------------------------------------- */

public sealed class EmailModel(PageServices s, Mailer mailer, SecretStore secrets) : AdminPage(s)
{
    public override string Title => "Email";
    public override string Intro => "How the dashboard sends alerts and digests: Exchange Online, or any SMTP server on any port. Passwords and secrets are stored encrypted in the database, never shown again, and needed by nothing but this.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var set = await S.Settings.AllAsync();
        var cfg = await mailer.ConfigAsync();
        string V(string k) => set.GetValueOrDefault(k) ?? "";
        var hasPw = !string.IsNullOrEmpty(cfg.Password) || (cfg.Mode == MailMode.Off && await secrets.GetAsync(SecretStore.SmtpPassword) is not null);
        var hasSecret = !string.IsNullOrEmpty(cfg.ClientSecret) || (cfg.Mode == MailMode.Off && await secrets.GetAsync(SecretStore.ExoClientSecret) is not null);
        var problems = cfg.Problems();
        var (host, port, tls) = cfg.Endpoint();

        Kpis.Add(new("Sending", cfg.Mode switch { MailMode.ExchangeOnline => "Exchange Online", MailMode.Smtp => "SMTP server", _ => "Off" },
            cfg.Mode == MailMode.Off ? null : $"{host}:{port}, {tls}", cfg.Mode == MailMode.Off ? "warn" : null));
        Kpis.Add(new("Ready", problems.Count == 0 ? "Yes" : "No", problems.Count == 0 ? null : string.Join(" ", problems), problems.Count == 0 ? null : "warn"));

        Panels.Add(new("How to send", new HtmlString(Form("mail",
            Select("MailMode", "Send email", ["Off", "ExchangeOnline", "Smtp"], V("MailMode") is "" ? "Off" : V("MailMode")) +
            Input("MailFrom", "From address", V("MailFrom"), 30) + Input("MailReplyTo", "Reply-To (optional)", V("MailReplyTo"), 30) +
            Input("DashboardUrl", "The dashboard's address, for links", V("DashboardUrl"), 36) +
            "<h3>Exchange Online</h3><p class=\"sub\">smtp.office365.com, port 587, STARTTLS. <b>Password</b>: SMTP AUTH with the mailbox's own password; the mailbox needs Authenticated SMTP allowed, and Microsoft turns SMTP AUTH with passwords off by default from the end of 2026. <b>OAuth</b>: an app registration (it can be a separate one) with the Office 365 Exchange Online permission SMTP.SendAsApp, registered in Exchange Online with New-ServicePrincipal and given Send As on the mailbox; no Microsoft Graph permission is involved.</p>" +
            Select("ExoAuth", "Sign in with", ["Password", "OAuth"], V("ExoAuth") is "" ? "Password" : V("ExoAuth")) +
            Input("ExoTenantId", "Tenant (OAuth)", V("ExoTenantId"), 30) + Input("ExoClientId", "App (client) id (OAuth)", V("ExoClientId"), 38) +
            Input("ExoClientSecret", hasSecret ? "Client secret (stored; leave empty to keep)" : "Client secret (OAuth)", null, 30, "password") +
            "<h3>SMTP server</h3><p class=\"sub\">Any server and port, such as an internal relay on 2525 that asks for a user name. Security: StartTls upgrades a plain connection (25, 587, 2525); Implicit is TLS from the first byte (465); Auto decides by port; None sends in the clear (a relay on the same network only).</p>" +
            Input("SmtpHost", "Server", V("SmtpHost"), 30) + Input("SmtpPort", "Port", V("SmtpPort") is "" ? "587" : V("SmtpPort"), 6, "number") +
            Select("SmtpSecurity", "Security", MailConfig.SecurityChoices, V("SmtpSecurity") is "" ? "StartTls" : V("SmtpSecurity")) +
            Input("SmtpUser", "User name (empty for none; for Exchange Online Password, the mailbox if not the From address)", V("SmtpUser"), 30) +
            Input("SmtpPassword", hasPw ? "Password (stored; leave empty to keep)" : "Password", null, 24, "password") +
            $"<label><input type=\"checkbox\" name=\"clearPassword\" value=\"1\"/> Remove the stored password and secret</label>" +
            $"<label><input type=\"checkbox\" name=\"SmtpAcceptAnyCertificate\" value=\"1\"{(V("SmtpAcceptAnyCertificate") == "1" ? " checked" : "")}/> Accept any server certificate (a lab relay)</label>" +
            Button("Save"))), "Nothing is sent while email is off; alerts are still recorded below and in the Windows event log."));

        Panels.Add(new("Send a test", new HtmlString(Form("test", Input("to", "To", Viewer.Upn, 30) + Button("Send a test email"))),
            "Sends one message with the settings saved above and records the result below.", "half"));

        var log = await Db.QueryAsync<(DateTime AtUtc, string Kind, string Subject, string? Recipients, string Status, string? Error)>(
            "SELECT TOP (50) AtUtc, Kind, Subject, Recipients, Status, Error FROM dash.NotificationLog ORDER BY LogId DESC;");
        var t = new Table { Headers = ["When (UTC)", "Kind", "Subject", "To", "Result", "Why not"], EmptyText = "Nothing sent yet." };
        foreach (var l in log) t.Rows.Add([l.AtUtc, l.Kind, l.Subject, l.Recipients, new HtmlString($"<span class=\"{(l.Status == "Sent" ? "status-ok" : "status-warn")}\">{E(l.Status)}</span>"), l.Error]);
        Panels.Add(new("Recently sent", t.Render(), "Kept for the days set in Admin > Privacy and retention."));
    }

    public Task<IActionResult> OnPostMailAsync(string? MailMode, string? MailFrom, string? MailReplyTo, string? DashboardUrl, string? ExoAuth, string? ExoTenantId,
        string? ExoClientId, string? ExoClientSecret, string? SmtpHost, string? SmtpPort, string? SmtpSecurity, string? SmtpUser, string? SmtpPassword,
        string? SmtpAcceptAnyCertificate, string? clearPassword) => AdminChangeAsync(async () =>
    {
        if (MailMode is not ("Off" or "ExchangeOnline" or "Smtp")) throw new ArgumentException("choose how to send.");
        if (MailFrom is { Length: > 0 } && MailConfig.Recipients(MailFrom).Count != 1) throw new ArgumentException("the From address is not an email address.");
        if (MailReplyTo is { Length: > 0 } && MailConfig.Recipients(MailReplyTo).Count != 1) throw new ArgumentException("the Reply-To address is not an email address.");
        if (DashboardUrl is { Length: > 0 } && !(Uri.TryCreate(DashboardUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
            throw new ArgumentException("the dashboard's address starts with https://.");
        if (ExoClientId is { Length: > 0 } && !Guid.TryParse(ExoClientId, out _)) throw new ArgumentException("the app (client) id is a GUID.");
        SettingWriter.Int(string.IsNullOrWhiteSpace(SmtpPort) ? "587" : SmtpPort, "The port", 1, 65535);
        if (SmtpSecurity is not null && !MailConfig.SecurityChoices.Contains(SmtpSecurity)) throw new ArgumentException("choose the security.");
        var by = Viewer.Upn ?? Viewer.WindowsName;
        var changed = await SettingWriter.SaveAsync(Db, S.Settings, by, new Dictionary<string, string?>
        {
            ["MailMode"] = MailMode, ["MailFrom"] = MailFrom, ["MailReplyTo"] = MailReplyTo, ["DashboardUrl"] = DashboardUrl?.TrimEnd('/'),
            ["ExoAuth"] = ExoAuth is "OAuth" ? "OAuth" : "Password", ["ExoTenantId"] = ExoTenantId, ["ExoClientId"] = ExoClientId,
            ["SmtpHost"] = SmtpHost, ["SmtpPort"] = string.IsNullOrWhiteSpace(SmtpPort) ? "587" : SmtpPort, ["SmtpSecurity"] = SmtpSecurity ?? "StartTls",
            ["SmtpUser"] = SmtpUser, ["SmtpAcceptAnyCertificate"] = SmtpAcceptAnyCertificate == "1" ? "1" : "0",
        });
        if (clearPassword == "1")
        {
            await secrets.SetAsync(SecretStore.SmtpPassword, null, by);
            await secrets.SetAsync(SecretStore.ExoClientSecret, null, by);
            changed.Add("password and secret removed");
        }
        if (!string.IsNullOrEmpty(SmtpPassword)) { await secrets.SetAsync(SecretStore.SmtpPassword, SmtpPassword, by); changed.Add("password"); }
        if (!string.IsNullOrEmpty(ExoClientSecret)) { await secrets.SetAsync(SecretStore.ExoClientSecret, ExoClientSecret, by); changed.Add("client secret"); }
        return SettingWriter.Saved(changed);
    });

    public Task<IActionResult> OnPostTestAsync(string? to) => AdminChangeAsync(async () =>
    {
        var rcpt = MailConfig.Recipients(to);
        if (rcpt.Count == 0) throw new ArgumentException("give an address to send the test to.");
        var e = new Email("Copilot Insights test message");
        e.Paragraph($"Sent by {Viewer.Name} from Admin > Email at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. If you can read this, alerts and digests will arrive.");
        e.Footer(null);
        var error = await mailer.SendAsync("Test", rcpt, e.Subject, e.Html(), e.Text());
        return error is null ? $"Test email sent to {rcpt[0].Address}." : $"The test email was not sent: {error}";
    });
}

/* ---- Alerts and digests ----------------------------------------------------------------------- */

public sealed class NotificationsModel(PageServices s, Notifier notifier, OpsReports reports) : AdminPage(s)
{
    public override string Title => "Alerts and digests";
    public override string Intro => "Who hears about what, when to raise an alert, and the digests sent on a schedule. Alerts are checked every five minutes and after each nightly refresh; each fires once per occurrence, by email and in the Windows Application log.";

    static readonly (string Key, string Label)[] Recipients =
    [
        ("AlertAdminTo", "Pipeline failures and stale data"), ("AlertSecurityTo", "Security signals"),
        ("AlertLicensingTo", "License pool running out; licenses on disabled accounts"), ("AlertAdoptionTo", "Drops and spikes in use"),
        ("AlertAgentsTo", "Agents used for the first time"),
    ];

    static readonly (string Key, string Label, int Min, int Max)[] Numbers =
    [
        ("FreshnessHours", "Data is stale after (hours)", 6, 240), ("AlertJailbreakPerDay", "Jailbreak attempts a day (0 off)", 0, 100000),
        ("AlertInjectionPerDay", "Prompt injections a day (0 off)", 0, 100000), ("AlertBlockedPerDay", "Blocked-resource interactions a day (0 off)", 0, 1000000),
        ("AlertLabelPerDay", "Interactions with sensitive labels a day", 1, 1000000), ("AlertSpikeFactor", "A signal spikes at (times its average)", 0, 100),
        ("AnomalyDropPercent", "A drop is at least (% below usual)", 0, 100), ("AnomalySpikePercent", "A spike is at least (% above usual)", 0, 10000),
        ("PoolWarnPercent", "The license pool is running out at (% assigned)", 50, 100),
        ("CertificateWarnDays", "Warn about the app certificate (days before it expires)", 7, 120),
    ];

    sealed class SubRow
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
        public bool Enabled { get; set; }
        public DateTime? LastSentUtc { get; set; }
        public string? LastStatus { get; set; }
    }

    protected override async Task BuildAsync()
    {
        ShowDone();
        var set = await S.Settings.AllAsync();
        string V(string k) => set.GetValueOrDefault(k) ?? "";
        Panels.Add(new("Who hears about what", new HtmlString(Form("recipients",
            string.Concat(Recipients.Select(r => Input(r.Key, r.Label, V(r.Key), 50))) + Button("Save"))),
            "Addresses separated by semicolons; a distribution list keeps it short. Email is set up in Admin > Email.", "half"));

        Panels.Add(new("When to alert", new HtmlString(Form("thresholds",
            string.Concat(Numbers.Select(n => Input(n.Key, n.Label, V(n.Key), 6, "number"))) +
            Input("AlertLabelRank", "Labels at or above this order are sensitive (empty off)", V("AlertLabelRank"), 4, "number") +
            $"<label><input type=\"checkbox\" name=\"NewAgentAlert\" value=\"1\"{(V("NewAgentAlert") != "0" ? " checked" : "")}/> Alert on agents used for the first time</label>" +
            $"<label><input type=\"checkbox\" name=\"EventLogAlerts\" value=\"1\"{(V("EventLogAlerts") != "0" ? " checked" : "")}/> Also write alerts to the Windows Application log (source Copilot Insights)</label>" +
            Button("Save"))),
            "A security signal alerts when a day reaches its threshold, or when it is the given number of times its 28-day average. Label order is set in Admin > Reference data.", "half"));

        var subs = await Db.QueryAsync<SubRow>("SELECT * FROM dash.Subscription ORDER BY Name;");
        var zone = await HttpContext.RequestServices.GetRequiredService<OpsData>().ZoneAsync();
        var t = new Table { Headers = ["Name", "Report", "To", "When", "For", "Days", "Last sent (UTC)", "Result", ""], EmptyText = "No digests yet." };
        foreach (var sub in subs)
            t.Rows.Add([sub.Name, sub.Report, sub.Recipients, When(sub), sub.RootUpn ?? "Whole organization", sub.Days, sub.LastSentUtc, sub.LastStatus,
                new HtmlString(
                    $"<a href=\"{E(Request.Path + $"?handler=preview&report={sub.Report}&days={sub.Days}&root={Uri.EscapeDataString(sub.RootUpn ?? "")}")}\">Preview</a> " +
                    Form("sendnow", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button("Send now", quiet: true)) +
                    Form("toggle", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button(sub.Enabled ? "Pause" : "Resume", quiet: true)) +
                    Form("remove", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button("Remove", $"Remove the digest {sub.Name}?", quiet: true)))]);
        var days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        Panels.Add(new("Digests", new HtmlString(t.Render().Value + "<h3>Add a digest</h3>" + Form("add",
            Input("name", "Name", null, 24) + Select("report", "Report", OpsReports.DigestKinds.Keys, "Leadership") + Input("recipients", "To", null, 40) +
            Select("frequency", "How often", Schedule.Frequencies, "Weekly") + Select("dow", "Day (weekly)", days, "Monday") +
            Input("dom", "Day of the month (monthly)", "1", 4, "number") + Input("hour", $"Hour ({zone.StandardName})", "7", 4, "number") +
            Select("days", "Covering", ReportContext.DayChoices.Select(d => $"{d}"), "30") +
            Input("root", "A manager's organization (UPN; empty for everyone)", null, 30) + Button("Add")) +
            "<ul class=\"sub\">" + string.Concat(OpsReports.DigestKinds.Select(k => $"<li><b>{E(k.Key)}</b>: {E(k.Value)}</li>")) + "</ul>"),
            "Digests hold aggregates only, never a named person, so they can go to anyone the scope is right for. Each covers the period chosen up to the last complete day."));

        var webhook = await HttpContext.RequestServices.GetRequiredService<SecretStore>().GetAsync(SecretStore.AlertWebhookUrl);
        Panels.Add(new("Teams, ServiceNow or any webhook", new HtmlString(Form("webhook",
            Input("url", webhook is null ? "Webhook address (https://)" : "Webhook address (stored; leave empty to keep)", null, 50, "password") +
            Select("AlertWebhookFormat", "Format", Webhook.Formats, V("AlertWebhookFormat") is "Json" ? "Json" : "Teams") +
            "<label><input type=\"checkbox\" name=\"clear\" value=\"1\"/> Remove the stored address</label>" + Button("Save")) +
            Form("webhooktest", Button("Post a test alert", quiet: true))),
            "Every alert is also posted here. Teams: a Workflows \"Post to a channel when a webhook request is received\" address (or an incoming webhook). Json: {source, kind, subject, lines, link, atUtc} for ServiceNow, Splunk or anything else. The address is stored encrypted, like the email password.", "half"));

        Panels.Add(new("Manager digests", new HtmlString(Form("digests",
            Select("ManagerDigest", "Every manager gets their organization's digest", ["Off", "Weekly", "Monthly"], V("ManagerDigest") is "" ? "Off" : V("ManagerDigest")) +
            Input("ManagerDigestMinPeople", "Managers with at least this many people under them", V("ManagerDigestMinPeople"), 5, "number") +
            Select("ManagerDigestDay", "Weekly on", days, days[Math.Clamp(int.TryParse(V("ManagerDigestDay"), out var md) ? md : 1, 0, 6)]) +
            Input("DigestHour", $"At (hour, {zone.StandardName})", V("DigestHour") is "" ? "7" : V("DigestHour"), 4, "number") + Button("Save"))),
            "The manager digest is the Leadership digest for the manager's own organization (7 days weekly, 30 monthly), aggregates only, to the manager's email address from AD. " +
            "When the dashboard is limited to levels (Admin > Access), only managers at those levels get it. Monthly digests go out on the 1st. Not sent in privacy mode.", "half"));

        /* Managers who asked not to get the digest (2.4.4): kept by administrators, now that the dashboard has no personal pages. */
        var optedOut = await Db.QueryAsync<(string Upn, string? DisplayName, DateTime UpdatedUtc)>(@"
SELECT pref.Upn, p.DisplayName, pref.UpdatedUtc FROM dash.DigestPreference AS pref LEFT JOIN dash.Person AS p ON p.Upn = pref.Upn
WHERE pref.ManagerDigestOptOut = 1 ORDER BY ISNULL(p.DisplayName, pref.Upn);");
        var ot = new Table { Headers = ["Manager", "Sign-in", "Since (UTC)", ""], EmptyText = "Every eligible manager gets the digest." };
        foreach (var o in optedOut)
            ot.Rows.Add([o.DisplayName, o.Upn, o.UpdatedUtc, new HtmlString(Form("optin", Hidden("upn", o.Upn) + Button("Send it again", quiet: true)))]);
        Panels.Add(new("Managers who do not get the digest", new HtmlString(Form("optout", Input("upn", "Manager (UPN)", null, 34) + Button("Stop their digest")) + ot.Render().Value),
            "For a manager who asks not to get it. Earlier opt-outs are kept.", "half"));

        var tenants = HttpContext.RequestServices.GetRequiredService<TenantAccessor>();
        Panels.Add(new("Run the checks now", new HtmlString(
            Form("run", Button(tenants.Many ? $"Run the checks for {tenants.Current.Name}" : "Run the checks now")) +
            (tenants.Many ? Form("runall", Button($"Run the checks for every tenant ({tenants.All.Count})", quiet: true)) : "")),
            tenants.Many
                ? "Runs one round of the alert checks and any digests that are due, for this tenant or for every tenant (the background checks cover every tenant every five minutes)."
                : "Runs one round of the alert checks and any digests that are due, instead of waiting up to five minutes.", "half"));
        var state = await Db.QueryAsync<(string AlertKey, string? LastValue, DateTime LastFiredUtc)>("SELECT AlertKey, LastValue, LastFiredUtc FROM dash.AlertState ORDER BY AlertKey;");
        var st = new Table { Headers = ["Check", "Last seen", "When (UTC)"], EmptyText = "The checks have not run yet." };
        foreach (var x in state) st.Rows.Add([x.AlertKey, x.LastValue, x.LastFiredUtc]);
        Panels.Add(new("Where the checks stand", st.Render(), "What each check last saw, so it does not repeat itself.", "half"));
    }

    static string When(SubRow s) => s.Frequency switch
    {
        "Weekly" => $"Weekly, {((DayOfWeek)(s.DayOfWeek ?? 1))} {s.HourLocal:00}:00",
        "Monthly" => $"Monthly, day {s.DayOfMonth ?? 1} {s.HourLocal:00}:00",
        _ => $"Daily {s.HourLocal:00}:00",
    } + (s.Enabled ? "" : " (paused)");

    public Task<IActionResult> OnPostRecipientsAsync() => AdminChangeAsync(async () =>
    {
        var values = Recipients.ToDictionary(r => r.Key, r => SettingWriter.Emails(Request.Form[r.Key], r.Label));
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, values));
    });

    public Task<IActionResult> OnPostThresholdsAsync() => AdminChangeAsync(async () =>
    {
        var values = new Dictionary<string, string?>();
        foreach (var n in Numbers) values[n.Key] = SettingWriter.Int(Request.Form[n.Key], n.Label, n.Min, n.Max).ToString(CultureInfo.InvariantCulture);
        var rank = Request.Form["AlertLabelRank"].ToString();
        values["AlertLabelRank"] = string.IsNullOrWhiteSpace(rank) ? null : SettingWriter.Int(rank, "The label order", 0, 250).ToString(CultureInfo.InvariantCulture);
        values["NewAgentAlert"] = Request.Form["NewAgentAlert"] == "1" ? "1" : "0";
        values["EventLogAlerts"] = Request.Form["EventLogAlerts"] == "1" ? "1" : "0";
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, values));
    });

    public Task<IActionResult> OnPostWebhookAsync(string? url, string? AlertWebhookFormat, string? clear) => AdminChangeAsync(async () =>
    {
        var by = Viewer.Upn ?? Viewer.WindowsName;
        if (Webhook.Problem(url) is { } bad) throw new ArgumentException(bad);
        var changed = await SettingWriter.SaveAsync(Db, S.Settings, by, new Dictionary<string, string?> { ["AlertWebhookFormat"] = AlertWebhookFormat is "Json" ? "Json" : "Teams" });
        var secrets = HttpContext.RequestServices.GetRequiredService<SecretStore>();
        if (clear == "1") { await secrets.SetAsync(SecretStore.AlertWebhookUrl, null, by); changed.Add("webhook address removed"); }
        if (!string.IsNullOrWhiteSpace(url)) { await secrets.SetAsync(SecretStore.AlertWebhookUrl, url.Trim(), by); changed.Add("webhook address"); }
        return SettingWriter.Saved(changed);
    });

    public Task<IActionResult> OnPostWebhooktestAsync() => AdminChangeAsync(async () =>
    {
        var url = await HttpContext.RequestServices.GetRequiredService<SecretStore>().GetAsync(SecretStore.AlertWebhookUrl);
        if (url is null) throw new ArgumentException("save a webhook address first.");
        var format = await S.Settings.StringAsync("AlertWebhookFormat") is "Json" ? "Json" : "Teams";
        var payload = Webhook.Payload(format, "Test", "Copilot Insights test alert", [$"Posted by {Viewer.Name} from Admin > Alerts and digests. Alerts will arrive here."], null, DateTime.UtcNow);
        using var http = HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            using var r = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), HttpContext.RequestAborted);
            return r.IsSuccessStatusCode ? $"Test alert posted ({format}): HTTP {(int)r.StatusCode}." : $"The webhook answered HTTP {(int)r.StatusCode} {r.ReasonPhrase}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return "The webhook could not be reached: " + ex.Message; }
    });

    public Task<IActionResult> OnPostDigestsAsync(string? ManagerDigest, string? ManagerDigestMinPeople, string? ManagerDigestDay, string? DigestHour) => AdminChangeAsync(async () =>
    {
        if (ManagerDigest is not ("Off" or "Weekly" or "Monthly")) throw new ArgumentException("choose Off, Weekly or Monthly.");
        var day = Array.IndexOf(new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" }, ManagerDigestDay);
        if (day < 0) throw new ArgumentException("choose the day.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["ManagerDigest"] = ManagerDigest,
            ["ManagerDigestMinPeople"] = SettingWriter.Int(ManagerDigestMinPeople, "The number of people", 1, 1000000).ToString(CultureInfo.InvariantCulture),
            ["ManagerDigestDay"] = day.ToString(CultureInfo.InvariantCulture),
            ["DigestHour"] = SettingWriter.Int(DigestHour, "The hour", 0, 23).ToString(CultureInfo.InvariantCulture),
        }));
    });

    public Task<IActionResult> OnPostOptoutAsync(string? upn) => AdminChangeAsync(async () =>
    {
        upn = upn?.Trim();
        if (string.IsNullOrEmpty(upn) || await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @upn", new { upn }) == 0)
            throw new ArgumentException($"{upn} is not in the directory.");
        await Db.ExecuteAsync(@"
UPDATE dash.DigestPreference SET ManagerDigestOptOut = 1, UpdatedUtc = SYSUTCDATETIME() WHERE Upn = @upn;
IF @@ROWCOUNT = 0 INSERT INTO dash.DigestPreference (Upn, ManagerDigestOptOut) VALUES (@upn, 1);", new { upn });
        return $"{upn} no longer gets the manager digest.";
    });

    public Task<IActionResult> OnPostOptinAsync(string? upn) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("UPDATE dash.DigestPreference SET ManagerDigestOptOut = 0, UpdatedUtc = SYSUTCDATETIME() WHERE Upn = @upn;", new { upn }) > 0
            ? $"{upn} gets the manager digest again." : $"{upn} was not opted out.");

    public Task<IActionResult> OnPostAddAsync(string? name, string? report, string? recipients, string? frequency, string? dow, int? dom, int? hour, int? days, string? root) => AdminChangeAsync(async () =>
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) throw new ArgumentException("give the digest a name.");
        if (report is null || !OpsReports.DigestKinds.ContainsKey(report)) throw new ArgumentException("choose a report.");
        var to = SettingWriter.Emails(recipients, "To") ?? throw new ArgumentException("give at least one address.");
        if (frequency is null || !Schedule.Frequencies.Contains(frequency)) throw new ArgumentException("choose how often.");
        var dayIndex = Array.IndexOf(new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" }, dow);
        if (hour is null or < 0 or > 23) throw new ArgumentException("the hour is 0 to 23.");
        if (dom is null or < 1 or > 28) throw new ArgumentException("the day of the month is 1 to 28.");
        if (days is null || !ReportContext.DayChoices.Contains(days.Value)) throw new ArgumentException("choose the period.");
        root = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
        if (root is not null && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @root", new { root }) == 0)
            throw new ArgumentException($"{root} is not in the directory.");
        await Db.ExecuteAsync(@"
INSERT INTO dash.Subscription (Name, Report, Recipients, Frequency, DayOfWeek, DayOfMonth, HourLocal, RootUpn, Days, CreatedBy, LastSentUtc)
VALUES (@name, @report, @to, @frequency, @dayIndex, @dom, @hour, @root, @days, @by, SYSUTCDATETIME());",
            new { name, report, to, frequency, dayIndex = dayIndex < 0 ? 1 : dayIndex, dom, hour, root, days, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Digest \"{name}\" added. The first goes out at its next scheduled time; Send now sends one today.";
    });

    public Task<IActionResult> OnPostSendnowAsync(int id) => AdminChangeAsync(async () =>
    {
        var sub = (await Db.QueryAsync<SubRow>("SELECT * FROM dash.Subscription WHERE SubscriptionId = @id", new { id })).FirstOrDefault() ?? throw new ArgumentException("no such digest.");
        var (subject, html, text) = await reports.DigestAsync(sub.Report, sub.Days, sub.RootUpn);
        var error = await HttpContext.RequestServices.GetRequiredService<Mailer>().SendAsync("Digest", MailConfig.Recipients(sub.Recipients), subject, html, text);
        return error is null ? $"Sent \"{subject}\" to {sub.Recipients}." : $"Not sent: {error}";
    });

    public Task<IActionResult> OnPostToggleAsync(int id) => AdminChangeAsync(async () =>
    {
        var n = await Db.ScalarAsync<string?>("UPDATE dash.Subscription SET Enabled = 1 - Enabled OUTPUT inserted.Name WHERE SubscriptionId = @id", new { id });
        return n is null ? "That digest was already gone." : $"Digest \"{n}\" paused or resumed.";
    });

    public Task<IActionResult> OnPostRemoveAsync(int id) => AdminChangeAsync(async () =>
    {
        var n = await Db.ScalarAsync<string?>("DELETE FROM dash.Subscription OUTPUT deleted.Name WHERE SubscriptionId = @id", new { id });
        return n is null ? "That digest was already gone." : $"Digest \"{n}\" removed.";
    });

    public Task<IActionResult> OnPostRunAsync() => AdminChangeAsync(async () =>
    {
        var done = await notifier.RunOnceAsync(HttpContext.RequestAborted);
        return done.Count == 0 ? "The checks ran: nothing to report." : "The checks ran. " + string.Join(" · ", done);
    });

    /// <summary>Every tenant, as the background checks do. Each tenant's lines start with its name.</summary>
    public Task<IActionResult> OnPostRunallAsync() => AdminChangeAsync(async () =>
    {
        var done = await notifier.RunAllAsync(HttpContext.RequestAborted);
        return done.Count == 0 ? "The checks ran in every tenant: nothing to report." : "The checks ran in every tenant. " + string.Join(" · ", done);
    });

    /// <summary>What a digest would look like now, as it would arrive.</summary>
    public async Task<IActionResult> OnGetPreviewAsync(string? report, int? days, string? root)
    {
        if (!await PrepareAsync()) return Page();
        if (report is null || !OpsReports.DigestKinds.ContainsKey(report)) return NotFound();
        var (_, html, _) = await reports.DigestAsync(report, days ?? 30, string.IsNullOrWhiteSpace(root) ? null : root);
        return Content(html, "text/html; charset=utf-8");
    }
}

/* ---- Targets and assumptions ------------------------------------------------------------------- */

public sealed class AssumptionsModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Targets and assumptions";
    public override string Intro => "The adoption targets progress is measured against, the minutes an interaction saves in each app and what an hour is worth, and the licensing group the license exports use.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var set = await S.Settings.AllAsync();
        string V(string k) => set.GetValueOrDefault(k) ?? "";
        var targets = await Db.QueryAsync<(int TargetId, string? RootUpn, string Metric, decimal TargetValue, DateTime StartDate, DateTime DueDate, string? Note, string CreatedBy)>(
            "SELECT TargetId, RootUpn, Metric, TargetValue, StartDate, DueDate, Note, CreatedBy FROM dash.Target ORDER BY DueDate;");
        var t = new Table { Headers = ["For", "Target", "From", "Due", "Note", "Set by", ""], EmptyText = "No targets yet." };
        foreach (var x in targets)
            t.Rows.Add([x.RootUpn ?? "Whole organization", $"{Adoption.TargetsModel.Label(x.Metric)} {x.TargetValue:0.#}{(x.Metric == "ActivePeople" ? "" : "%")}", x.StartDate, x.DueDate, x.Note, x.CreatedBy,
                new HtmlString(Form("deltarget", $"<input type=\"hidden\" name=\"id\" value=\"{x.TargetId}\"/>" + Button("Remove", "Remove this target?", quiet: true)))]);
        Panels.Add(new("Adoption targets", new HtmlString(t.Render().Value + "<h3>Add a target</h3>" + Form("target",
            Input("root", "A manager's organization (UPN; empty for everyone)", null, 30) +
            Select("metric", "Measure", ["Adoption", "ActiveShare", "ActivePeople"], "Adoption") + Input("value", "Target (% or people)", null, 6, "number") +
            Input("start", "From", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") + Input("due", "Due", DateTime.Today.AddMonths(3).ToString("yyyy-MM-dd"), 10, "date") +
            Input("note", "Note", null, 30) + Button("Add"))),
            "Adoption: licensed people using it, over people licensed. ActiveShare: people using it, over everyone. ActivePeople: a count. Progress is on Adoption > Targets."));

        var apps = await Db.QueryAsync<(string App, decimal? Minutes)>("SELECT a.App, ts.Minutes FROM bi.DimApp AS a LEFT JOIN dash.TimeSaving AS ts ON ts.App = a.App ORDER BY a.App;");
        var at = new Table { Headers = ["App", "Minutes saved per interaction"] };
        foreach (var a in apps)
            at.Rows.Add([a.App, new HtmlString(Form("minutes", $"<input type=\"hidden\" name=\"app\" value=\"{E(a.App)}\"/>" +
                Input("minutes", "Minutes (empty: the default)", a.Minutes?.ToString("0.##", CultureInfo.InvariantCulture), 5) + Button("Save", quiet: true)))]);
        Panels.Add(new("Time saved", new HtmlString(Form("value",
            Input("MinutesSavedDefault", "Minutes one interaction saves (default)", V("MinutesSavedDefault"), 5) +
            Input("HourlyCost", $"What an hour is worth ({V("LicenseCurrency")})", V("HourlyCost"), 6) + Button("Save")) + at.Render().Value),
            "Your organization's own estimates, from a survey or a pilot: every figure on Leadership > Value follows them. Microsoft's and analysts' published studies are a starting point, not a measurement of your tenant."));

        Panels.Add(new("Licensing and champions", new HtmlString(Form("licensing",
            Input("LicenseGroupId", "Object id of the Entra group that assigns Copilot licenses (empty if licenses are assigned directly)", V("LicenseGroupId"), 38) +
            Input("ChampionsPerDepartment", "Champions to suggest per department", V("ChampionsPerDepartment"), 4, "number") +
            Input("RolloutWaveSize", "People in a rollout wave", V("RolloutWaveSize"), 6, "number") + Button("Save"))),
            "With a group, the license exports add and remove its members; without one, they assign or remove the license itself. The license price is in Admin > Access.", "half"));
    }

    public Task<IActionResult> OnPostTargetAsync(string? root, string? metric, string? value, DateTime? start, DateTime? due, string? note) => AdminChangeAsync(async () =>
    {
        root = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
        if (root is not null && await Db.ScalarAsync<int>("SELECT COUNT(*) FROM dash.Person WHERE Upn = @root", new { root }) == 0)
            throw new ArgumentException($"{root} is not in the directory.");
        if (metric is not ("Adoption" or "ActiveShare" or "ActivePeople")) throw new ArgumentException("choose a measure.");
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0 || (metric != "ActivePeople" && v > 100))
            throw new ArgumentException("the target is a percentage up to 100, or a number of people.");
        if (start is null || due is null || due <= start) throw new ArgumentException("the due date comes after the start.");
        await Db.ExecuteAsync("INSERT INTO dash.Target (RootUpn, Metric, TargetValue, StartDate, DueDate, Note, CreatedBy) VALUES (@root, @metric, @v, @start, @due, @note, @by);",
            new { root, metric, v, start, due, note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)], by = Viewer.Upn ?? Viewer.WindowsName });
        return $"Target added: {Adoption.TargetsModel.Label(metric)} {v:0.#} for {root ?? "the whole organization"} by {due:yyyy-MM-dd}.";
    });

    public Task<IActionResult> OnPostDeltargetAsync(int id) => AdminChangeAsync(async () =>
        await Db.ExecuteAsync("DELETE FROM dash.Target WHERE TargetId = @id", new { id }) > 0 ? "Target removed." : "That target was already gone.");

    public Task<IActionResult> OnPostValueAsync(string? MinutesSavedDefault, string? HourlyCost) => AdminChangeAsync(async () =>
    {
        if (!decimal.TryParse(MinutesSavedDefault, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) || m < 0 || m > 240) throw new ArgumentException("the default minutes are 0 to 240.");
        if (!decimal.TryParse(HourlyCost, NumberStyles.Number, CultureInfo.InvariantCulture, out var h) || h < 0 || h > 100000) throw new ArgumentException("the hourly cost is a number.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["MinutesSavedDefault"] = m.ToString(CultureInfo.InvariantCulture), ["HourlyCost"] = h.ToString(CultureInfo.InvariantCulture),
        }));
    });

    public Task<IActionResult> OnPostMinutesAsync(string? app, string? minutes) => AdminChangeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(app) || await Db.ScalarAsync<int>("SELECT COUNT(*) FROM bi.DimApp WHERE App = @app", new { app }) == 0) throw new ArgumentException("no such app.");
        if (string.IsNullOrWhiteSpace(minutes))
        {
            await Db.ExecuteAsync("DELETE FROM dash.TimeSaving WHERE App = @app", new { app });
            return $"{app} uses the default minutes.";
        }
        if (!decimal.TryParse(minutes, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) || m < 0 || m > 240) throw new ArgumentException("minutes are 0 to 240.");
        await Db.ExecuteAsync(@"
UPDATE dash.TimeSaving SET Minutes = @m, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE App = @app;
IF @@ROWCOUNT = 0 INSERT INTO dash.TimeSaving (App, Minutes, UpdatedBy) VALUES (@app, @m, @by);", new { app, m, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"{app}: {m:0.##} minutes an interaction.";
    });

    public Task<IActionResult> OnPostLicensingAsync(string? LicenseGroupId, string? ChampionsPerDepartment, string? RolloutWaveSize) => AdminChangeAsync(async () =>
    {
        if (!string.IsNullOrWhiteSpace(LicenseGroupId) && !Guid.TryParse(LicenseGroupId, out _)) throw new ArgumentException("the group's object id is a GUID.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["LicenseGroupId"] = LicenseGroupId, ["ChampionsPerDepartment"] = SettingWriter.Int(ChampionsPerDepartment, "Champions per department", 1, 20).ToString(CultureInfo.InvariantCulture),
            ["RolloutWaveSize"] = SettingWriter.Int(string.IsNullOrWhiteSpace(RolloutWaveSize) ? "50" : RolloutWaveSize, "People in a wave", 1, 100000).ToString(CultureInfo.InvariantCulture),
        }));
    });
}

/* ---- Privacy and retention --------------------------------------------------------------------- */

public sealed class PrivacyModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Privacy and retention";
    public override string Intro => "Privacy mode for organizations that must not report on named people (a works council's condition, for example), how long logs and stored JSON are kept, and the data API.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var set = await S.Settings.AllAsync();
        string V(string k) => set.GetValueOrDefault(k) ?? "";
        var on = await S.Settings.PrivacyModeAsync();
        Kpis.Add(new("Privacy mode", on ? "On" : "Off", on ? "nobody is named but the viewer themselves" : null));
        Panels.Add(new("Privacy mode", new HtmlString(Form("privacy",
            Select("mode", "Privacy mode", ["Off", "Pseudonymize"], on ? "Pseudonymize" : "Off") + Button("Save", "Change privacy mode? It applies to every viewer and to Power BI at its next refresh."))),
            "On: every page, export, digest and API call that names people is refused, and Power BI's Users table shows a salted hash instead of names, sign-ins and managers, the same for the same person, so counts and org slicing still work. Aggregates keep the minimum group size (Admin > Access)."));

        Panels.Add(new("Retention", new HtmlString(Form("retention",
            Input("AuditLogRetentionDays", "Keep the dashboard's audit log (days; 0 keeps it)", V("AuditLogRetentionDays"), 6, "number") +
            Input("NotificationRetentionDays", "Keep the notification log (days; 0 keeps it)", V("NotificationRetentionDays"), 6, "number") +
            Input("JsonRetentionDays", "Clear the stored JSON of records older than (days; 0 keeps it; at least 200)", V("JsonRetentionDays"), 6, "number") +
            Button("Save")) + Form("apply", Button("Apply retention now", "Delete what is past retention now?", quiet: true))),
            "Applied each night after a successful run. Clearing the JSON keeps every report working (the flattened table has the fields); it cannot be fetched again once Purview's own 180 days have passed, hence the 200-day floor."));

        Panels.Add(new("Data API", new HtmlString(Form("api",
            $"<label><input type=\"checkbox\" name=\"ApiEnabled\" value=\"1\"{(V("ApiEnabled") != "0" ? " checked" : "")}/> Answer the read-only data API at /api/v1</label>" + Button("Save"))),
            "The same sign-in, scope and roles as the pages; every call is in the audit log. /api/v1 lists the endpoints.", "half"));
    }

    public Task<IActionResult> OnPostPrivacyAsync(string? mode) => AdminChangeAsync(async () =>
    {
        if (mode is not ("Off" or "Pseudonymize")) throw new ArgumentException("choose Off or Pseudonymize.");
        await Db.ExecuteAsync("EXEC dash.usp_SetPrivacyMode @Mode = @mode, @By = @by;", new { mode, by = Viewer.Upn ?? Viewer.WindowsName });
        S.Settings.Invalidate();
        return mode == "Off" ? "Privacy mode is off." : "Privacy mode is on: nobody is named. Power BI's Users table is pseudonymized from its next refresh.";
    });

    public Task<IActionResult> OnPostRetentionAsync(string? AuditLogRetentionDays, string? NotificationRetentionDays, string? JsonRetentionDays) => AdminChangeAsync(async () =>
    {
        var json = SettingWriter.Int(JsonRetentionDays, "JSON retention", 0, 36500);
        if (json is > 0 and < 200) throw new ArgumentException("JSON retention is 0 (keep) or at least 200 days: past Purview's 180 days it cannot be fetched again.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["AuditLogRetentionDays"] = SettingWriter.Int(AuditLogRetentionDays, "Audit-log retention", 0, 36500).ToString(CultureInfo.InvariantCulture),
            ["NotificationRetentionDays"] = SettingWriter.Int(NotificationRetentionDays, "Notification-log retention", 0, 36500).ToString(CultureInfo.InvariantCulture),
            ["JsonRetentionDays"] = json.ToString(CultureInfo.InvariantCulture),
        }));
    });

    public Task<IActionResult> OnPostApplyAsync() => AdminChangeAsync(async () =>
    {
        try { return "Retention applied: " + await Db.ScalarAsync<string>("EXEC dash.usp_ApplyRetention;"); }
        catch (Microsoft.Data.SqlClient.SqlException ex) { return "Retention stopped: " + ex.Message + " The nightly run applies it as the pipeline's account."; }
    });

    public Task<IActionResult> OnPostApiAsync(string? ApiEnabled) => AdminChangeAsync(async () =>
        SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?> { ["ApiEnabled"] = ApiEnabled == "1" ? "1" : "0" })));
}

/* ---- Value names ------------------------------------------------------------------------------ */

public sealed class ValuesModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Value names";
    public override string Intro => "Readable names for the codes the audit log records: label ids, tenant, app and agent ids, capacities, license types. Every column here has fewer than 10,000 distinct values; names show in the dashboard and in Power BI (bi.ValueNames).";

    sealed class ColRow
    {
        public short ColumnId { get; set; }
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string Label { get; set; } = "";
        public int DistinctValues { get; set; }
        public bool Capped { get; set; }
        public int Named { get; set; }
    }

    sealed class ValRow
    {
        public string Value { get; set; } = "";
        public long Records { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string? Name { get; set; }
        public string? Note { get; set; }
        public int TotalRows { get; set; }
    }

    const string NameExpr = "COALESCE(n.Name, CASE WHEN v.ColumnId IN (11, 30) THEN sl.Name WHEN v.ColumnId = 6 THEN aa.FriendlyName END)";
    const string Joins = @"LEFT JOIN dash.ValueName AS n ON n.ColumnId = v.ColumnId AND n.Value = v.Value
LEFT JOIN dash.SensitivityLabel AS sl ON v.ColumnId IN (11, 30) AND sl.LabelId = v.Value
LEFT JOIN bi.DimAgent AS ag ON v.ColumnId = 6 AND ag.AgentId = v.Value
LEFT JOIN dash.AgentAlias AS aa ON aa.AgentKey = ag.AgentKey";

    protected override async Task BuildAsync()
    {
        ShowDone();
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.ValueCatalogColumn', N'U') IS NULL THEN 0 ELSE 1 END") == 0)
        {
            Notice = "The value catalog is not installed yet: run Install-CopilotAudit.ps1 from this release.";
            return;
        }
        var cols = await Db.QueryAsync<ColRow>($@"
SELECT c.ColumnId, c.TableName, c.ColumnName, c.Label, c.DistinctValues, c.Capped,
       (SELECT COUNT(*) FROM audit.ValueCatalog AS v {Joins} WHERE v.ColumnId = c.ColumnId AND {NameExpr} IS NOT NULL) AS Named
FROM audit.ValueCatalogColumn AS c ORDER BY c.ColumnId;");
        var col = short.TryParse(Request.Query["col"], out var cid) ? cols.FirstOrDefault(c => c.ColumnId == cid) : null;
        var ct = new Table { Headers = ["Column", "In the flattened data", "Values", "Named", ""], Numeric = new HashSet<int> { 2, 3 } };
        foreach (var c in cols.Where(c => c.DistinctValues > 0 || c.Capped))
            ct.Rows.Add([c.Label, $"{c.TableName}.{c.ColumnName}", c.Capped ? "10,000 or more" : $"{c.DistinctValues:#,0}", c.Named,
                c.Capped ? "too many to name" : new Link(c.ColumnId == col?.ColumnId ? "Naming" : "Name them", Request.Path + $"?col={c.ColumnId}")]);
        Panels.Add(new("Columns", ct.Render(), "Columns with no values yet are hidden. The catalog fills as records are flattened; for data flattened before it existed, run Invoke-CopilotMaintenance.ps1 -Task RebuildValueCatalog once."));
        if (col is null || col.Capped) return;

        var pg = new Paging(Request, new Dictionary<string, string> { ["most"] = "v.Records DESC", ["unnamed"] = $"CASE WHEN {NameExpr} IS NULL THEN 0 ELSE 1 END, v.Records DESC", ["value"] = "v.Value", ["new"] = "v.FirstSeenUtc DESC" }, "unnamed");
        var vals = await Db.QueryAsync<ValRow>($@"
SELECT v.Value, v.Records, v.FirstSeenUtc, v.LastSeenUtc, {NameExpr} AS Name, n.Note, COUNT(*) OVER () AS TotalRows
FROM audit.ValueCatalog AS v {Joins}
WHERE v.ColumnId = @id AND (@q IS NULL OR v.Value LIKE @q OR {NameExpr} LIKE @q)
{pg.OrderAndPage()};", new { id = col.ColumnId, q = pg.Like });
        pg.Total = vals.FirstOrDefault()?.TotalRows ?? 0;
        var vt = new Table { Headers = ["Value", "Times seen", "First seen", "Last seen", "Name"], Numeric = new HashSet<int> { 1 }, EmptyText = "No values match." };
        foreach (var v in vals)
            vt.Rows.Add([v.Value, v.Records, v.FirstSeenUtc.Date, v.LastSeenUtc.Date, new HtmlString(Form("name",
                $"<input type=\"hidden\" name=\"col\" value=\"{col.ColumnId}\"/><input type=\"hidden\" name=\"value\" value=\"{E(v.Value)}\"/>" +
                Input("name", "Name", v.Name, 26) + Input("note", "Note", v.Note, 20) + Button("Save", quiet: true), $"col={col.ColumnId}"))]);
        var upload = $"<form class=\"admin\" method=\"post\" enctype=\"multipart/form-data\" action=\"{E(Request.Path + "?handler=upload&col=" + col.ColumnId)}\">{Token()}" +
                     "<label>Names from a CSV (column id, value, name, note)<input type=\"file\" name=\"file\" accept=\".csv,text/csv\"/></label>" + Button("Upload") + "</form>" +
                     $"<p class=\"sub\"><a href=\"{E(Request.Path + $"?handler=download&col={col.ColumnId}")}\">Download this column's values and names as CSV</a>, fill in the names, and upload it back.</p>";
        Panels.Add(new(col.Label, new HtmlString(upload + pg.Controls(Request, new Dictionary<string, string> { ["unnamed"] = "Unnamed first", ["most"] = "Most seen", ["value"] = "Value", ["new"] = "Newest" }).Value + vt.Render().Value),
            col.ColumnId is 11 or 30 ? "Label names are shared with Admin > Reference data, where their order is set." : col.ColumnId == 6 ? "Agent names are shared with Admin > Reference data." : "Clear a name to remove it."));
    }

    async Task<string> SaveAsync(short col, string value, string? name, string? note)
    {
        var by = Viewer.Upn ?? Viewer.WindowsName;
        if (await Db.ScalarAsync<int>("SELECT COUNT(*) FROM audit.ValueCatalog WHERE ColumnId = @col AND Value = @value", new { col, value }) == 0)
            throw new ArgumentException($"'{value}' is not a value of that column.");
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(200, name.Trim().Length)];
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(400, note.Trim().Length)];
        if (col is 11 or 30)
        {
            /* Sensitivity labels keep their names (and order) in dash.SensitivityLabel. */
            if (name is null) await Db.ExecuteAsync("DELETE FROM dash.SensitivityLabel WHERE LabelId = @value", new { value });
            else await Db.ExecuteAsync(@"
UPDATE dash.SensitivityLabel SET Name = @name, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE LabelId = @value;
IF @@ROWCOUNT = 0 INSERT INTO dash.SensitivityLabel (LabelId, Name, UpdatedBy) VALUES (@value, @name, @by);", new { value, name, by });
        }
        else if (col == 6 && await Db.ScalarAsync<int?>("SELECT AgentKey FROM bi.DimAgent WHERE AgentId = @value", new { value }) is int key)
        {
            if (name is null) await Db.ExecuteAsync("DELETE FROM dash.AgentAlias WHERE AgentKey = @key", new { key });
            else await Db.ExecuteAsync(@"
UPDATE dash.AgentAlias SET FriendlyName = @name, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE AgentKey = @key;
IF @@ROWCOUNT = 0 INSERT INTO dash.AgentAlias (AgentKey, FriendlyName, UpdatedBy) VALUES (@key, @name, @by);", new { key, name, by });
        }
        if (name is null) await Db.ExecuteAsync("DELETE FROM dash.ValueName WHERE ColumnId = @col AND Value = @value", new { col, value });
        else await Db.ExecuteAsync(@"
UPDATE dash.ValueName SET Name = @name, Note = @note, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE ColumnId = @col AND Value = @value;
IF @@ROWCOUNT = 0 INSERT INTO dash.ValueName (ColumnId, Value, Name, Note, UpdatedBy) VALUES (@col, @value, @name, @note, @by);", new { col, value, name, note, by });
        return name is null ? $"Name cleared for {value}." : $"{value} is now \"{name}\".";
    }

    public Task<IActionResult> OnPostNameAsync(short col, string? value, string? name, string? note) => AdminChangeAsync(() =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException("no value.") : SaveAsync(col, value, name, note));

    public Task<IActionResult> OnPostUploadAsync(IFormFile? file) => AdminChangeAsync(async () =>
    {
        if (file is null || file.Length == 0) throw new ArgumentException("choose a CSV file.");
        if (file.Length > 5_000_000) throw new ArgumentException("the file is over 5 MB.");
        using var r = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var (rows, errors) = CsvUpload.ValueNames(await r.ReadToEndAsync());
        int saved = 0, cleared = 0;
        foreach (var row in rows)
        {
            try
            {
                await SaveAsync(row.ColumnId, row.Value, row.Name, row.Note);
                if (string.IsNullOrWhiteSpace(row.Name)) cleared++; else saved++;
            }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
        }
        return $"{saved} name(s) saved, {cleared} left without a name." + (errors.Count == 0 ? "" : $" {errors.Count} line(s) skipped: " + string.Join(" ", errors.Take(5)) + (errors.Count > 5 ? $" And {errors.Count - 5} more." : ""));
    });

    public async Task<IActionResult> OnGetDownloadAsync(short col)
    {
        if (!await PrepareAsync()) return Page();
        var vals = await Db.QueryAsync<(string Value, string? Name, string? Note)>($@"
SELECT v.Value, {NameExpr}, n.Note FROM audit.ValueCatalog AS v {Joins} WHERE v.ColumnId = @col ORDER BY v.Records DESC;", new { col });
        var t = new Table { Headers = ["ColumnId", "Value", "Name", "Note"] };
        foreach (var v in vals) t.Rows.Add([(int)col, v.Value, v.Name, v.Note]);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, null, $"value names, column {col}: {vals.Count} values");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(t.ToCsv())).ToArray(), "text/csv", $"value-names-{col}.csv");
    }
}

/* ---- Training records ---------------------------------------------------------------------------- */

public sealed class TrainingRecordsModel(PageServices s) : AdminPage(s)
{
    public override string Title => "Training records";
    public override string Intro => "Who attended which Copilot training, from a CSV your learning system exports: the Training effect report compares their use before and after with their colleagues'.";

    protected override async Task BuildAsync()
    {
        ShowDone();
        var sessions = await Db.QueryAsync<(string Session, DateTime AttendedOn, int People, DateTime Imported)>(
            "SELECT Session, AttendedOn, COUNT(*), MAX(ImportedUtc) FROM dash.TrainingAttendance GROUP BY Session, AttendedOn ORDER BY AttendedOn DESC;");
        var t = new Table { Headers = ["Session", "Date", "Attendees", "Imported (UTC)", ""], Numeric = new HashSet<int> { 2 }, EmptyText = "No training records yet." };
        foreach (var x in sessions)
            t.Rows.Add([x.Session, x.AttendedOn, x.People, x.Imported, new HtmlString(Form("remove",
                $"<input type=\"hidden\" name=\"session\" value=\"{E(x.Session)}\"/><input type=\"hidden\" name=\"date\" value=\"{x.AttendedOn:yyyy-MM-dd}\"/>" +
                Button("Remove", $"Remove the {x.People} attendance records of {x.Session} on {x.AttendedOn:yyyy-MM-dd}?", quiet: true)))]);
        var upload = $"<form class=\"admin\" method=\"post\" enctype=\"multipart/form-data\" action=\"{E(Request.Path + "?handler=upload")}\">{Token()}" +
                     "<label>Attendance CSV: UPN, session, date (yyyy-MM-dd)<input type=\"file\" name=\"file\" accept=\".csv,text/csv\"/></label>" + Button("Upload") + "</form>" +
                     $"<p class=\"sub\"><a href=\"{E(Request.Path + "?handler=template")}\">Download a template</a>. A header row is fine; a record already there is skipped.</p>";
        Panels.Add(new("Sessions", new HtmlString(upload + t.Render().Value), "The effect is measured 28 days after each session, so a session shows on Enablement > Training effect four weeks after it."));
    }

    public Task<IActionResult> OnPostUploadAsync(IFormFile? file) => AdminChangeAsync(async () =>
    {
        if (file is null || file.Length == 0) throw new ArgumentException("choose a CSV file.");
        if (file.Length > 20_000_000) throw new ArgumentException("the file is over 20 MB.");
        using var r = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var (rows, errors) = CsvUpload.Training(await r.ReadToEndAsync());
        var added = 0;
        var by = Viewer.Upn ?? Viewer.WindowsName;
        foreach (var chunk in rows.Chunk(500))
            added += await Db.ExecuteAsync(@"
INSERT INTO dash.TrainingAttendance (Upn, Session, AttendedOn, ImportedBy)
SELECT s.Upn, s.Session, s.AttendedOn, @by
FROM OPENJSON(@json) WITH (Upn NVARCHAR(256) '$.u', Session NVARCHAR(190) '$.s', AttendedOn DATE '$.d') AS s
WHERE NOT EXISTS (SELECT 1 FROM dash.TrainingAttendance AS t WHERE t.Upn = s.Upn AND t.Session = s.Session AND t.AttendedOn = s.AttendedOn)
GROUP BY s.Upn, s.Session, s.AttendedOn;",
                new { by, json = System.Text.Json.JsonSerializer.Serialize(chunk.Select(x => new { u = x.Upn, s = x.Session, d = x.Date.ToString("yyyy-MM-dd") })) });
        return $"{added} attendance record(s) added from {rows.Count} line(s)." + (errors.Count == 0 ? "" : $" {errors.Count} line(s) skipped: " + string.Join(" ", errors.Take(5)) + (errors.Count > 5 ? $" And {errors.Count - 5} more." : ""));
    });

    public Task<IActionResult> OnPostRemoveAsync(string? session, DateTime? date) => AdminChangeAsync(async () =>
    {
        var n = await Db.ExecuteAsync("DELETE FROM dash.TrainingAttendance WHERE Session = @session AND AttendedOn = @date", new { session, date });
        return $"{n} attendance record(s) of {session} removed.";
    });

    public async Task<IActionResult> OnGetTemplateAsync()
    {
        if (!await PrepareAsync()) return Page();
        return File(Encoding.UTF8.GetBytes("UPN,Session,Date\r\nada.lovelace@contoso.com,Copilot in Outlook,2026-10-01\r\n"), "text/csv", "training-attendance-template.csv");
    }
}
