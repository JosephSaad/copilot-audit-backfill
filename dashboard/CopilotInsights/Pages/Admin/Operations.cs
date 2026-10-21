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
    /// <param name="showValues">
    /// Each change with its old and new value ("AuditLogRetentionDays 730 -> 400"), for the audit log (2.4.12): where the
    /// value itself is what an administrator is held to account for, such as how long the audit log is kept or where
    /// mail is sent. Otherwise only the names.
    /// </param>
    public static async Task<List<string>> SaveAsync(Db db, Settings settings, string by, IReadOnlyDictionary<string, string?> values, bool showValues = false)
    {
        var before = await settings.AllAsync();
        var changed = new List<string>();
        foreach (var (name, raw) in values)
        {
            var v = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            var old = before.GetValueOrDefault(name);
            if (old == v) continue;
            await db.ExecuteAsync(@"
UPDATE dash.Setting SET Value = @v, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Name = @n;
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, UpdatedBy) VALUES (@n, @v, @by);", new { n = name, v, by });
            changed.Add(showValues ? $"{name} {old ?? "(none)"} -> {v ?? "(none)"}" : name);
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

        Kpis.Add(new("Email sent through", cfg.Mode switch { MailMode.ExchangeOnline => "Exchange Online", MailMode.Smtp => "SMTP server", _ => "Off" },
            cfg.Mode == MailMode.Off ? null : $"{host}:{port}, {tls}", cfg.Mode == MailMode.Off ? "warn" : null));
        Kpis.Add(new("Ready to send", problems.Count == 0 ? "Yes" : "No", problems.Count == 0 ? null : string.Join(" ", problems), problems.Count == 0 ? null : "warn"));

        Panels.Add(new("How to send", new HtmlString(Form("mail",
            Select("MailMode", "Send email", [("Off", "Off"), ("ExchangeOnline", "Through Exchange Online"), ("Smtp", "Through an SMTP server")], V("MailMode") is "" ? "Off" : V("MailMode")) +
            Input("MailFrom", "From address", V("MailFrom"), 30) + Input("MailReplyTo", "Reply-To (optional)", V("MailReplyTo"), 30) +
            Input("DashboardUrl", "The dashboard's address, for links", V("DashboardUrl"), 36) +
            "<h3>Exchange Online</h3><p class=\"sub\">smtp.office365.com, port 587, STARTTLS. <b>Password</b>: SMTP AUTH with the mailbox's own password; the mailbox needs Authenticated SMTP allowed, and Microsoft turns SMTP AUTH with passwords off by default from the end of 2026. <b>OAuth</b>: an app registration (it can be a separate one) with the Office 365 Exchange Online permission SMTP.SendAsApp, registered in Exchange Online with New-ServicePrincipal and given Send As on the mailbox; no Microsoft Graph permission is involved.</p>" +
            Select("ExoAuth", "Sign in with", ["Password", "OAuth"], V("ExoAuth") is "" ? "Password" : V("ExoAuth")) +
            Input("ExoTenantId", "Tenant (OAuth)", V("ExoTenantId"), 30) + Input("ExoClientId", "App (client) id (OAuth)", V("ExoClientId"), 38) +
            Input("ExoClientSecret", hasSecret ? "Client secret (stored; leave empty to keep)" : "Client secret (OAuth)", null, 30, "password") +
            "<h3>SMTP server</h3><p class=\"sub\">Any server and port, such as an internal relay on 2525 that asks for a user name. Security: StartTls upgrades a plain connection (25, 587, 2525); Implicit is TLS from the first byte (465); Auto decides by port; None sends in the clear (a relay on the same network only). " +
            "A stored password is sent only to the server, port, security and user name it was typed in with: change any of them, and type it again.</p>" +
            Input("SmtpHost", "Server", V("SmtpHost"), 30) + Input("SmtpPort", "Port", V("SmtpPort") is "" ? "587" : V("SmtpPort"), 6, "number") +
            Select("SmtpSecurity", "Security", MailConfig.SecurityChoices, V("SmtpSecurity") is "" ? "StartTls" : V("SmtpSecurity")) +
            Input("SmtpUser", "User name (empty for none; for Exchange Online Password, the mailbox if not the From address)", V("SmtpUser"), 30) +
            Input("SmtpPassword", cfg.PasswordMoved ? "Password (stored for another server or user; type it again)" : hasPw ? "Password (stored; leave empty to keep)" : "Password", null, 24, "password") +
            $"<label><input type=\"checkbox\" name=\"clearPassword\" value=\"1\"/> Remove the stored password and secret</label>" +
            $"<label><input type=\"checkbox\" name=\"SmtpAcceptAnyCertificate\" value=\"1\"{(V("SmtpAcceptAnyCertificate") == "1" ? " checked" : "")}/> Accept any server certificate (a lab relay)</label>" +
            Button("Save"))), "Nothing is sent while email is off; alerts are still recorded below and in the Windows event log."));

        Panels.Add(new("Send a test", new HtmlString(Form("test", Input("to", "To", Viewer.Upn, 30) + Button("Send a test email"))),
            "Sends one message with the settings saved above and records the result below.", "half"));

        /* The conversation with the mail server is kept from 2.6.2 (dash.NotificationLog.Detail); a database not yet upgraded has none. */
        var kept = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.NotificationLog', N'Detail') IS NULL THEN 0 ELSE 1 END") == 1;
        var log = await Db.QueryAsync<(DateTime AtUtc, string Kind, string Subject, string? Recipients, string Status, string? Error, string? Detail)>(
            $"SELECT TOP (50) AtUtc, Kind, Subject, Recipients, Status, Error, {(kept ? "Detail" : "CAST(NULL AS NVARCHAR(MAX))")} FROM dash.NotificationLog ORDER BY LogId DESC;");

        /* Where to look when email does not arrive: the last test's whole conversation, open on the page. */
        var last = log.FirstOrDefault(l => l.Kind == "Test" && l.Detail is not null);
        var where = "<ul class=\"sub\">" +
            "<li><b>This page, Recently sent</b>: every message the dashboard tried to send, with why not. For a test, and for any message that was not sent, <i>show</i> under Conversation opens what was said to the mail server and what it answered.</li>" +
            "<li><b>The last test</b>, below: send a test above, and its conversation is shown here whole. Read it from the top: the server and port, the security used, who signed in, then each command (C:) and the server's answer (S:). The line that fails is the last one.</li>" +
            "<li><b>The database</b>: the same rows are in dash.NotificationLog (Status, Error, Detail), kept for the days set in Admin › Privacy and retention.</li>" +
            "<li><b>The web server's Windows Application log</b>, source <i>Copilot Insights</i>: each alert, sent or not; and source <i>.NET Runtime</i> or <i>IIS AspNetCore Module V2</i> if the site itself failed.</li></ul>" +
            "<p class=\"sub\">Common answers: <i>535</i> or <i>Authentication unsuccessful</i> is the user name or password, or the server does not allow that sign-in; no <i>250-AUTH</i> line after EHLO means the server offers no sign-in on that port, or only after STARTTLS; " +
            "<i>530 Must issue a STARTTLS command first</i> needs Security StartTls; a certificate that is not trusted needs the relay's certificate trusted on this web server (or, in a lab, Accept any server certificate); " +
            "<i>550 5.7.60</i> or <i>SendAsDenied</i> means the account may not send as the From address; nothing after <i>Server ...</i> means the server and port were never reached (name, firewall).</p>";
        Panels.Add(new("Troubleshooting: where the logs are", new HtmlString(where +
            (last.Detail is null ? "<p class=\"muted\">No test has been sent since the conversation began to be kept. Send a test above.</p>"
                : $"<h3>The last test, {E(When(last.AtUtc))}: {E(last.Status == "Sent" ? "sent" : "not sent")}</h3><pre class=\"log whole\">{E(last.Detail)}</pre>")),
            "The password is never in a conversation, nor the message itself."));

        var t = new Table { Zone = Zone, Headers = ["When (UTC)", "Kind", "Subject", "To", "Result", "Why not", "Conversation"], EmptyText = "Nothing sent yet." };
        foreach (var l in log)
            t.Rows.Add([l.AtUtc, l.Kind, l.Subject, l.Recipients, new HtmlString($"<span class=\"{(l.Status == "Sent" ? "status-ok" : "status-warn")}\">{E(l.Status)}</span>"), l.Error,
                l.Detail is null ? null : new HtmlString($"<details><summary>show</summary><pre class=\"log\">{E(l.Detail)}</pre></details>")]);
        Panels.Add(new("Recently sent", t.Render(), "Kept for the days set in Admin › Privacy and retention. The conversation with the mail server is kept for a test and for a message that was not sent."));
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
        var values = new Dictionary<string, string?>
        {
            ["MailMode"] = MailMode, ["MailFrom"] = MailFrom, ["MailReplyTo"] = MailReplyTo, ["DashboardUrl"] = DashboardUrl?.TrimEnd('/'),
            ["ExoAuth"] = ExoAuth is "OAuth" ? "OAuth" : "Password", ["ExoTenantId"] = ExoTenantId, ["ExoClientId"] = ExoClientId,
            ["SmtpHost"] = SmtpHost, ["SmtpPort"] = string.IsNullOrWhiteSpace(SmtpPort) ? "587" : SmtpPort, ["SmtpSecurity"] = SmtpSecurity ?? "StartTls",
            ["SmtpUser"] = SmtpUser, ["SmtpAcceptAnyCertificate"] = SmtpAcceptAnyCertificate == "1" ? "1" : "0",
        };
        var now = MailConfig.Load(await S.Settings.AllAsync(), await secrets.GetAsync(SecretStore.SmtpPassword), null, await secrets.GetAsync(SecretStore.SmtpPasswordFor));
        var next = MailConfig.Load(values, null, null);
        /* The secrets first (2.4.12): one SQL Server cannot store stops the save before any setting has changed. */
        var notes = new List<string>();
        if (clearPassword == "1")
        {
            await secrets.SetAsync(SecretStore.SmtpPassword, null, by);
            await secrets.SetAsync(SecretStore.SmtpPasswordFor, null, by);
            await secrets.SetAsync(SecretStore.ExoClientSecret, null, by);
            notes.Add("password and secret removed");
        }
        if (!string.IsNullOrEmpty(SmtpPassword))
        {
            /* A password goes only where it was entered for (2.4.12): these settings' server, port, security and user. */
            await secrets.SetAsync(SecretStore.SmtpPassword, SmtpPassword, by);
            await secrets.SetAsync(SecretStore.SmtpPasswordFor, next.PasswordTarget(), by);
            notes.Add("password");
        }
        else if (clearPassword != "1" && !string.IsNullOrEmpty(now.Password))
        {
            /* One stored before 2.4.12 is tied to where it goes now, before this save can move it. */
            if (now.PasswordFor is null) await secrets.SetAsync(SecretStore.SmtpPasswordFor, now.PasswordTarget(), by);
            if (next.Mode != Infrastructure.MailMode.Off && (now.PasswordFor ?? now.PasswordTarget()) != next.PasswordTarget())
                notes.Add("the stored password is not sent to this server, port, security or user name until it is typed again");
        }
        if (!string.IsNullOrEmpty(ExoClientSecret)) { await secrets.SetAsync(SecretStore.ExoClientSecret, ExoClientSecret, by); notes.Add("client secret"); }
        var changed = await SettingWriter.SaveAsync(Db, S.Settings, by, values, showValues: true);
        changed.AddRange(notes);
        return SettingWriter.Saved(changed);
    });

    public Task<IActionResult> OnPostTestAsync(string? to) => AdminChangeAsync(async () =>
    {
        var rcpt = MailConfig.Recipients(to);
        if (rcpt.Count == 0) throw new ArgumentException("give an address to send the test to.");
        var e = new Email("Copilot Insights test message");
        e.Paragraph($"Sent by {Viewer.Name} from Admin › Email at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. If you can read this, alerts and digests will arrive.");
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
            "Addresses separated by semicolons; a distribution list keeps it short. Email is set up in Admin › Email.", "half"));

        Panels.Add(new("When to alert", new HtmlString(Form("thresholds",
            string.Concat(Numbers.Select(n => Input(n.Key, n.Label, V(n.Key), 6, "number"))) +
            Input("AlertLabelRank", "Labels at or above this order are sensitive (empty off)", V("AlertLabelRank"), 4, "number") +
            $"<label><input type=\"checkbox\" name=\"NewAgentAlert\" value=\"1\"{(V("NewAgentAlert") != "0" ? " checked" : "")}/> Alert on agents used for the first time</label>" +
            $"<label><input type=\"checkbox\" name=\"EventLogAlerts\" value=\"1\"{(V("EventLogAlerts") != "0" ? " checked" : "")}/> Also write alerts to the Windows Application log (source Copilot Insights)</label>" +
            Button("Save"))),
            "A security signal alerts when a day reaches its threshold, or when it is the given number of times its 28-day average. Label order is set in Admin › Reference data.", "half"));

        var subs = await Db.QueryAsync<SubRow>("SELECT * FROM dash.Subscription ORDER BY Name;");
        var zone = await HttpContext.RequestServices.GetRequiredService<OpsData>().ZoneAsync();
        var t = new Table { Zone = Zone, Headers = ["Name", "Report", "To", "When", "For", "Days", "Last sent (UTC)", "Result", ""], EmptyText = "No digests yet." };
        foreach (var sub in subs)
            t.Rows.Add([sub.Name, sub.Report, sub.Recipients, When(sub), sub.RootUpn ?? "Whole organization", sub.Days, sub.LastSentUtc, sub.LastStatus,
                new HtmlString("<div class=\"acts\">" +
                    $"<a href=\"{E(Request.Path + $"?handler=preview&report={sub.Report}&days={sub.Days}&root={Uri.EscapeDataString(sub.RootUpn ?? "")}")}\">Preview</a> " +
                    Form("sendnow", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button("Send now", quiet: true)) +
                    Form("toggle", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button(sub.Enabled ? "Pause" : "Resume", quiet: true)) +
                    Form("remove", $"<input type=\"hidden\" name=\"id\" value=\"{sub.SubscriptionId}\"/>" + Button("Remove", $"Remove the digest {sub.Name}?", quiet: true)) + "</div>")]);
        var days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        Panels.Add(new("Digests", new HtmlString(t.Render().Value + "<h3>Add a digest</h3>" + Form("add",
            Input("name", "Name", null, 24) + Select("report", "Report", OpsReports.DigestKinds.Keys, "Leadership") + Input("recipients", "To", null, 40) +
            Select("frequency", "How often", Schedule.Frequencies, "Weekly") + Select("dow", "Day (weekly)", days, "Monday") +
            Input("dom", "Day of the month (monthly)", "1", 4, "number") + Input("hour", $"Hour ({zone.StandardName})", "7", 4, "number") +
            Select("days", "Covering", ReportContext.DayChoices.Select(d => $"{d}"), "30") +
            Input("root", "A manager's organization (UPN; empty for everyone)", null, 30) + Button("Add")) +
            "<ul class=\"sub\">" + string.Concat(OpsReports.DigestKinds.Select(k => $"<li><b>{E(k.Key)}</b>: {E(k.Value)}</li>")) + "</ul>"),
            "Digests hold aggregates only, never a named person, so they can go to anyone who may see that part of the organization. Each covers the period chosen up to the last complete day."));

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
            "When the dashboard is limited to levels (Admin › Access), only managers at those levels get it. Monthly digests go out on the 1st. Not sent in privacy mode.", "half"));

        /* Managers who asked not to get the digest (2.4.4): kept by administrators, now that the dashboard has no personal pages. */
        var optedOut = await Db.QueryAsync<(string Upn, string? DisplayName, DateTime UpdatedUtc)>(@"
SELECT pref.Upn, p.DisplayName, pref.UpdatedUtc FROM dash.DigestPreference AS pref LEFT JOIN dash.Person AS p ON p.Upn = pref.Upn
WHERE pref.ManagerDigestOptOut = 1 ORDER BY ISNULL(p.DisplayName, pref.Upn);");
        var ot = new Table { Zone = Zone, Headers = ["Manager", "Sign-in", "Since (UTC)", ""], EmptyText = "Every eligible manager gets the digest." };
        foreach (var o in optedOut)
            ot.Rows.Add([o.DisplayName, o.Upn, o.UpdatedUtc, new HtmlString(Form("optin", Hidden("upn", o.Upn) + Button("Send it again", quiet: true)))]);
        Panels.Add(new("Managers who do not get the digest", new HtmlString(Form("optout", PersonBox("upn", "Manager (name or UPN)", null, 34) + Button("Stop their digest")) + ot.Render().Value),
            "For a manager who asks not to get it. Earlier opt-outs are kept.", "half"));

        var tenants = HttpContext.RequestServices.GetRequiredService<TenantAccessor>();
        Panels.Add(new("Run the checks now", new HtmlString(
            Form("run", Button(tenants.Many ? $"Run the checks for {tenants.Current.Name}" : "Run the checks now")) +
            (tenants.Many ? Form("runall", Button($"Run the checks for every tenant ({tenants.All.Count})", quiet: true)) : "")),
            tenants.Many
                ? "Runs one round of the alert checks and any digests that are due, for this tenant or for every tenant you are an administrator in (the background checks cover every tenant every five minutes)."
                : "Runs one round of the alert checks and any digests that are due, instead of waiting up to five minutes.", "half"));
        var state = await Db.QueryAsync<(string AlertKey, string? LastValue, DateTime LastFiredUtc)>("SELECT AlertKey, LastValue, LastFiredUtc FROM dash.AlertState ORDER BY AlertKey;");
        var st = new Table { Zone = Zone, Headers = ["Check", "Last seen", "When (UTC)"], EmptyText = "The checks have not run yet." };
        /* An alert waiting to be emailed again, and the web servers that run the checks (2.4.12), in words. */
        foreach (var x in state)
            st.Rows.Add(x.AlertKey.StartsWith("retry:", StringComparison.Ordinal) ? [$"{x.AlertKey["retry:".Length..]}: email to try again", $"{x.LastValue} of {Retry.Tries} tries failed", x.LastFiredUtc]
                : x.AlertKey.StartsWith("server:", StringComparison.Ordinal) ? [$"Checks run on {x.AlertKey["server:".Length..]}", "last round", x.LastFiredUtc]
                : [x.AlertKey, x.LastValue, x.LastFiredUtc]);
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
        /* The address first (2.4.12): one SQL Server cannot store stops the save before the format has changed. */
        var secrets = HttpContext.RequestServices.GetRequiredService<SecretStore>();
        var notes = new List<string>();
        if (clear == "1") { await secrets.SetAsync(SecretStore.AlertWebhookUrl, null, by); notes.Add("webhook address removed"); }
        if (!string.IsNullOrWhiteSpace(url)) { await secrets.SetAsync(SecretStore.AlertWebhookUrl, url.Trim(), by); notes.Add("webhook address"); }
        var changed = await SettingWriter.SaveAsync(Db, S.Settings, by, new Dictionary<string, string?> { ["AlertWebhookFormat"] = AlertWebhookFormat is "Json" ? "Json" : "Teams" });
        changed.AddRange(notes);
        return SettingWriter.Saved(changed);
    });

    public Task<IActionResult> OnPostWebhooktestAsync() => AdminChangeAsync(async () =>
    {
        var url = await HttpContext.RequestServices.GetRequiredService<SecretStore>().GetAsync(SecretStore.AlertWebhookUrl);
        if (url is null) throw new ArgumentException("save a webhook address first.");
        var format = await S.Settings.StringAsync("AlertWebhookFormat") is "Json" ? "Json" : "Teams";
        var payload = Webhook.Payload(format, "Test", "Copilot Insights test alert", [$"Posted by {Viewer.Name} from Admin › Alerts and digests. Alerts will arrive here."], null, DateTime.UtcNow);
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
        /* Held to the same rule as Send now and Preview (2.4.12): an administrator without the section's role set up a
           Security digest to their own address, and it went out on its schedule. */
        if (DigestRefusal(report) is { } no) throw new ArgumentException(no);
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

    /// <summary>
    /// The role a digest's figures need (2.4.12): that of the section it shows, as on the section's pages; none for
    /// Leadership and Value, whose pages need none.
    /// </summary>
    public static string? DigestRole(string report) => report switch
    {
        "Adoption" => Roles.Adoption, "Licensing" => Roles.Licensing, "Security" => Roles.Security, _ => null,
    };

    /// <summary>Why this administrator may not preview or send this digest now, or null (2.4.12).</summary>
    string? DigestRefusal(string report) => DigestRole(report) is { } role && !Viewer.Has(role)
        ? $"the {report} digest shows the {role} section, which needs the {role} role. An administrator can grant it in Admin › Access."
        : null;

    public Task<IActionResult> OnPostSendnowAsync(int id) => AdminChangeAsync(async () =>
    {
        var sub = (await Db.QueryAsync<SubRow>("SELECT * FROM dash.Subscription WHERE SubscriptionId = @id", new { id })).FirstOrDefault() ?? throw new ArgumentException("no such digest.");
        if (DigestRefusal(sub.Report) is { } no) throw new ArgumentException(no);
        var (subject, html, text) = await reports.DigestAsync(sub.Report, sub.Days, sub.RootUpn);
        var error = await HttpContext.RequestServices.GetRequiredService<Mailer>().SendAsync("Digest", MailConfig.Recipients(sub.Recipients), subject, html, text);
        return error is null ? $"Sent \"{subject}\" to {sub.Recipients}." : $"Not sent: {error}";
    });

    public Task<IActionResult> OnPostToggleAsync(int id) => AdminChangeAsync(async () =>
    {
        /* Resuming one is held to the rule for adding one (2.4.12); pausing one never is. */
        var sub = (await Db.QueryAsync<(string Report, bool Enabled)>("SELECT Report, Enabled FROM dash.Subscription WHERE SubscriptionId = @id", new { id })).FirstOrDefault();
        if (sub.Report is not null && !sub.Enabled && DigestRefusal(sub.Report) is { } no) throw new ArgumentException(no);
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

    /// <summary>
    /// Every tenant where this person is an administrator (2.4.12), decided in each tenant's own database as everything else
    /// is; each tenant's lines start with its name, and each tenant it ran in has the run in its own audit log. Before, an
    /// administrator of one tenant ran the round in every tenant and read back their results.
    /// </summary>
    public Task<IActionResult> OnPostRunallAsync() => AdminChangeAsync(async () =>
    {
        var tenants = HttpContext.RequestServices.GetRequiredService<TenantAccessor>();
        var from = tenants.Current.Name;
        var done = await EveryTenantAsync(tenants, () => S.Viewers.ResolveAsync(), () => notifier.RunOnceAsync(HttpContext.RequestAborted),
            (v, lines) => S.Audit.WriteAsync(v, "Admin", Request.Path, null, $"The checks ran, from tenant {from}. " + string.Join(" · ", lines)), Request.Path);
        return done.Count == 0 ? "The checks ran: nothing to report." : "The checks ran. " + string.Join(" · ", done);
    });

    /// <summary>
    /// Runs <paramref name="run"/> in each tenant where <paramref name="viewer"/> is an administrator, and says so for the
    /// others; <paramref name="record"/> writes the run into another tenant's audit log (this tenant's is the page's).
    /// An administrator there is a full one, or one whose Admin pages there include <paramref name="page"/> (2.6.4).
    /// </summary>
    public static async Task<List<string>> EveryTenantAsync(TenantAccessor tenants, Func<Task<Viewer>> viewer, Func<Task<List<string>>> run,
        Func<Viewer, IReadOnlyList<string>, Task> record, string? page = null)
    {
        var here = tenants.Current;
        var done = new List<string>();
        try
        {
            foreach (var t in tenants.All)
            {
                tenants.Use(t);
                string Line(string d) => tenants.Many ? $"[{t.Name}] {d}" : d;
                try
                {
                    var v = await viewer();
                    if (!v.CanAdmin(page)) { done.Add(Line("Not run: you are not an administrator in this tenant.")); continue; }
                    var lines = await run();
                    if (t.Name != here.Name) await record(v, lines);
                    done.AddRange(lines.Select(Line));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { done.Add(Line("The checks failed: " + ex.Message)); }
            }
        }
        finally { tenants.Use(here); }
        return done;
    }

    /// <summary>
    /// What a digest would look like now, as it would arrive: for an administrator who may see its figures, and in the
    /// audit log (2.4.12). Before, any administrator saw any digest for any part of the organization, the Security
    /// digest without the Security role, and nothing recorded that they had.
    /// </summary>
    public async Task<IActionResult> OnGetPreviewAsync(string? report, int? days, string? root)
    {
        if (!await PrepareAsync()) return Page();
        if (report is null || !OpsReports.DigestKinds.ContainsKey(report)) return NotFound();
        root = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
        static ContentResult Html(string body, int status = 200) =>
            new() { Content = body, ContentType = "text/html; charset=utf-8", StatusCode = status };
        if ((Viewer.Admitted ? DigestRefusal(report) : LimitRefusal(Viewer)) is { } no)
        {
            await S.Audit.WriteAsync(Viewer, "Denied", Request.Path, root ?? "Whole organization", $"digest preview of {report}: {no}");
            return Html($"<!DOCTYPE html><p>Not shown: {E(no)}</p>", 403);
        }
        try
        {
            var (subject, html, _) = await reports.DigestAsync(report, days ?? 30, root);
            await S.Audit.WriteAsync(Viewer, "View", Request.Path, root ?? "Whole organization", $"digest preview: {subject}");
            return Html(html);
        }
        catch (DigestRefused ex)
        {
            await S.Audit.WriteAsync(Viewer, "View", Request.Path, root ?? "Whole organization", $"digest preview of {report}, not shown: {ex.Message}");
            return Html($"<!DOCTYPE html><p>Not shown: {E(ex.Message)}.</p>");
        }
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
            /* The measures by the names the table above and Adoption › Targets give them (2.4.10: the box showed their codes). */
            Select("metric", "Measure", new[] { "Adoption", "ActiveShare", "ActivePeople" }.Select(m => (m, Adoption.TargetsModel.Label(m))), "Adoption") +
            Input("value", "Target (% or people)", null, 6, "number") +
            Input("start", "From", DateTime.Today.ToString("yyyy-MM-dd"), 10, "date") + Input("due", "Due", DateTime.Today.AddMonths(3).ToString("yyyy-MM-dd"), 10, "date") +
            Input("note", "Note", null, 30) + Button("Add"))),
            "Adoption: license holders who used Copilot, as a share of all license holders. Share of people active: everyone who used Copilot, as a share of everyone. " +
            "People active: a number of people, not a share. Progress is on Adoption › Targets."));

        var apps = await Db.QueryAsync<(string App, decimal? Minutes)>("SELECT a.App, ts.Minutes FROM bi.DimApp AS a LEFT JOIN dash.TimeSaving AS ts ON ts.App = a.App ORDER BY a.App;");
        var at = new Table { Headers = ["App", "Minutes saved per interaction"] };
        foreach (var a in apps)
            at.Rows.Add([a.App, new HtmlString(Form("minutes", $"<input type=\"hidden\" name=\"app\" value=\"{E(a.App)}\"/>" +
                Input("minutes", "Minutes (empty: the default)", a.Minutes?.ToString("0.##", CultureInfo.InvariantCulture), 5) + Button("Save", quiet: true)))]);
        Panels.Add(new("Time saved", new HtmlString(Form("value",
            Input("MinutesSavedDefault", "Minutes one interaction saves (default)", V("MinutesSavedDefault"), 5) +
            Input("HourlyCost", $"What an hour is worth ({V("LicenseCurrency")})", V("HourlyCost"), 6) + Button("Save")) + at.Render().Value),
            "Your organization's own estimates, from a survey or a pilot: every figure on Leadership › Value follows them. Microsoft's and analysts' published studies are a starting point, not a measurement of your tenant."));

        Panels.Add(new("Licensing and champions", new HtmlString(Form("licensing",
            Input("LicenseGroupId", "Object id of the Entra group that assigns Copilot licenses (empty if licenses are assigned directly)", V("LicenseGroupId"), 38) +
            Input("ChampionsPerDepartment", "Champions to suggest per department", V("ChampionsPerDepartment"), 4, "number") +
            Input("RolloutWaveSize", "People in a rollout wave", V("RolloutWaveSize"), 6, "number") + Button("Save"))),
            "With a group, the license exports add and remove its members; without one, they assign or remove the license itself. The license price is in Admin › Access.", "half"));

        /* 2.6.6: what Leadership › Costs and savings prices and values with. */
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CostPrice', N'U') IS NULL THEN 0 ELSE 1 END;") == 1)
        {
            /* 2.6.9: with each price, its volume price and the Dataverse capacity a seat brings, where the columns are installed. */
            var tiers = await Db.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH(N'dash.CostPrice', N'SparePercent') IS NULL THEN 0 ELSE 1 END;") == 1;
            var prices = await Db.QueryAsync<(string Item, string Area, string Name, string Unit, decimal MonthlyPrice, int? TierFrom, decimal? TierPrice, decimal? DbGb, decimal? FileGb, decimal? Spare, bool? ForCapacity)>(tiers
                ? "SELECT Item, Area, Name, Unit, MonthlyPrice, TierFromSeats, TierPrice, DatabaseGbPerSeat, FileGbPerSeat, SparePercent, ForCapacity FROM dash.CostPrice ORDER BY Area, Name;"
                : "SELECT Item, Area, Name, Unit, MonthlyPrice, CAST(NULL AS INT), CAST(NULL AS DECIMAL(19, 4)), CAST(NULL AS DECIMAL(9, 3)), CAST(NULL AS DECIMAL(9, 3)), CAST(NULL AS DECIMAL(5, 2)), CAST(NULL AS BIT) FROM dash.CostPrice ORDER BY Area, Name;");
            static string D(decimal? v, string f = "0.####") => v?.ToString(f, CultureInfo.InvariantCulture) ?? "";
            var pt = new Table { Headers = ["Area", "What", "Item (SKU part number)", "Per", $"Price ({V("LicenseCurrency")})" + (tiers ? ", volume price, and Dataverse capacity a seat brings" : "")] };
            foreach (var p in prices)
                pt.Rows.Add([p.Area, p.Name, p.Item, p.Unit, new HtmlString(Form("price", Hidden("item", p.Item) +
                    Input("price", $"Price a {p.Unit}", p.MonthlyPrice.ToString("0.####", CultureInfo.InvariantCulture), 7) +
                    (tiers && p.Unit == "user"
                        ? Input("tierFrom", "From seats (volume)", p.TierFrom?.ToString(CultureInfo.InvariantCulture), 6, "number") + Input("tierPrice", "Volume price", D(p.TierPrice), 6) +
                          Input("dbGb", "Database GB a seat", D(p.DbGb, "0.###"), 5) + Input("fileGb", "File GB a seat", D(p.FileGb, "0.###"), 5) +
                          Input("spare", "Spare kept (%; empty: the default)", D(p.Spare, "0.##"), 5) +
                          Select("capacity", "Bought for capacity or the volume price", [("0", "No"), ("1", "Yes")], p.ForCapacity == true ? "1" : "0")
                        : "") +
                    Button("Save", quiet: true)))]);
            Panels.Add(new("Costs and savings", new HtmlString(Form("costs",
                Input("FabricReservedDiscount", "Fabric reservation discount, as a fraction (0.41 is 41%)", V("FabricReservedDiscount"), 6) +
                Input("FlowMinutesPerRun", "Minutes one flow run saves (default)", V("FlowMinutesPerRun"), 5) +
                Input("PowerBiCopilotMinutes", "Minutes one Copilot in Power BI request saves", V("PowerBiCopilotMinutes"), 5) +
                Input("CostIdleDays", "Idle after (days): GitHub seats, Power Platform licenses, AI accounts", V("CostIdleDays"), 4, "number") +
                Input("SpareSeatPercent", "Spare seats kept unassigned (% of those bought)", V("SpareSeatPercent"), 4) + Button("Save")) + pt.Render().Value),
                "What Leadership › Costs and savings counts with. The prices are list prices in USD when installed: put in what your agreement pays, a month for a license or a GB, " +
                "and per credit for a credit. A license is matched to Graph's license pool by its SKU part number. Hourly cost is under Time saved, above. " +
                "Volume price: from that many seats every seat costs the volume price (Power Apps Premium: 12 from 2,000), so a cut under it is counted at what it really saves. " +
                "Dataverse capacity a seat brings: what each seat adds to the tenant's Dataverse pool (Power Apps and Power Automate Premium: 0.25 GB of database, 2 GB of file); a cut that would take the pool under what is used costs add-on capacity, taken off its saving. " +
                "Spare seats: unassigned seats kept on purpose for joiners and moves are not a saving; the default share is above, and a license can set its own. Bought for capacity or the volume price: its unassigned and idle seats are never counted as money back."));
        }

        /* 2.6.6: what Licensing › Credit use estimates credits and tokens with. */
        Panels.Add(new("Credit estimate", new HtmlString(Form("credits",
            Input("CreditsPerAnswer", "Credits a billed agent answer costs", V("CreditsPerAnswer"), 4) +
            Input("CreditsPerGrounding", "Credits grounding in work data adds", V("CreditsPerGrounding"), 4) +
            Input("CreditsPerAction", "Credits an agent action adds", V("CreditsPerAction"), 4) +
            Input("TokensPerTurn", "Tokens a turn takes", V("TokensPerTurn"), 7, "number") +
            Input("TokensPerResource", "Tokens each resource read adds", V("TokensPerResource"), 7, "number") +
            Input("TokensPerAnswer", "Tokens an answer gives back", V("TokensPerAnswer"), 7, "number") + Button("Save"))),
            "What Licensing › Credit use estimates with. Credits: Microsoft's list rates are 2 for a generative answer, 10 more for grounding in the organization's data, 5 for an action; " +
            "a credit's price is the Copilot Credit, pay-as-you-go, under Costs and savings. Tokens are a rough figure (Microsoft reports none for Microsoft 365 Copilot): what a turn takes before grounding, what each file or mail read adds, and an answer.", "half"));

        /* 2.6.6: the suites, their price, what they include, and on or off. */
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.CostSuite', N'U') IS NULL THEN 0 ELSE 1 END;") == 1)
        {
            var suites = await Db.QueryAsync<(string SkuPattern, string Suite, byte Tier, decimal MonthlyPrice, string Includes, bool Enabled)>(
                "SELECT SkuPattern, Suite, Tier, MonthlyPrice, Includes, Enabled FROM dash.CostSuite ORDER BY Family, Tier, SkuPattern;");
            var st = new Table { Headers = ["Suite", "SKU part number (pattern)", "Tier", $"Price a user a month ({V("LicenseCurrency")}), includes, and on or off"] };
            foreach (var s in suites)
                st.Rows.Add([s.Suite, s.SkuPattern, (int)s.Tier, new HtmlString(Form("suite", Hidden("pattern", s.SkuPattern) +
                    Input("price", "Price", s.MonthlyPrice.ToString("0.##", CultureInfo.InvariantCulture), 6) +
                    Input("includes", "Includes (COPILOT, POWER_BI_PRO)", s.Includes, 22) +
                    Select("enabled", "Counted", [("1", "On"), ("0", "Off")], s.Enabled ? "1" : "0") + Button("Save", quiet: true)))]);
            Panels.Add(new("Suites: E3, E5 and E7", st.Render(),
                "The suites people hold, matched to Graph's license pool by SKU part number (% matches anything). Includes: what of the priced items a suite already includes, so a standalone one on its holder is paid twice, " +
                "and Copilot or Power BI Pro inside it is not counted again as AI spend: COPILOT for Microsoft 365 Copilot, POWER_BI_PRO for Power BI Pro. The price is what Licensing › Suite fit compares a move down with; " +
                "set your agreement's. Off leaves a suite out of both pages. The E7 rows are Microsoft's announced part numbers' likely forms: check License pool for yours, and the license lookup's -CopilotSuiteSku with it."));
        }
    }

    public Task<IActionResult> OnPostCreditsAsync(string? CreditsPerAnswer, string? CreditsPerGrounding, string? CreditsPerAction, string? TokensPerTurn, string? TokensPerResource, string? TokensPerAnswer) => AdminChangeAsync(async () =>
    {
        static decimal Num(string? v, string what, decimal min, decimal max) =>
            decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= min && d <= max ? d : throw new ArgumentException($"{what} is a number from {min} to {max}.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["CreditsPerAnswer"] = Num(CreditsPerAnswer, "the credits an answer costs", 0, 100).ToString(CultureInfo.InvariantCulture),
            ["CreditsPerGrounding"] = Num(CreditsPerGrounding, "the credits grounding adds", 0, 100).ToString(CultureInfo.InvariantCulture),
            ["CreditsPerAction"] = Num(CreditsPerAction, "the credits an action adds", 0, 100).ToString(CultureInfo.InvariantCulture),
            ["TokensPerTurn"] = SettingWriter.Int(TokensPerTurn, "Tokens a turn", 0, 200000).ToString(CultureInfo.InvariantCulture),
            ["TokensPerResource"] = SettingWriter.Int(TokensPerResource, "Tokens a resource", 0, 200000).ToString(CultureInfo.InvariantCulture),
            ["TokensPerAnswer"] = SettingWriter.Int(TokensPerAnswer, "Tokens an answer", 0, 200000).ToString(CultureInfo.InvariantCulture),
        }));
    });

    public Task<IActionResult> OnPostSuiteAsync(string? pattern, string? price, string? includes, string? enabled) => AdminChangeAsync(async () =>
    {
        if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) || p < 0 || p > 100000) throw new ArgumentException("the price is a number, such as 60.");
        var items = (includes ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Any(i => i.Length > 64 || !i.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))) throw new ArgumentException("includes is a list of item names separated by commas, such as COPILOT,POWER_BI_PRO.");
        var name = await Db.ScalarAsync<string?>("SELECT Suite FROM dash.CostSuite WHERE SkuPattern = @pattern;", new { pattern }) ?? throw new ArgumentException("no such suite.");
        var on = enabled == "1";
        await Db.ExecuteAsync("UPDATE dash.CostSuite SET MonthlyPrice = @p, Includes = @inc, Enabled = @on, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE SkuPattern = @pattern;",
            new { p, inc = string.Join(",", items.Select(i => i.ToUpperInvariant())), on, pattern, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"{name} ({pattern}): {p.ToString("0.##", CultureInfo.InvariantCulture)}, {(on ? "on" : "off")}.";
    });

    public Task<IActionResult> OnPostCostsAsync(string? FabricReservedDiscount, string? FlowMinutesPerRun, string? PowerBiCopilotMinutes, string? CostIdleDays, string? SpareSeatPercent) => AdminChangeAsync(async () =>
    {
        static decimal Num(string? v, string what, decimal min, decimal max) =>
            decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= min && d <= max ? d : throw new ArgumentException($"{what} is a number from {min} to {max}.");
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["FabricReservedDiscount"] = Num(FabricReservedDiscount, "the reservation discount", 0, 0.9m).ToString(CultureInfo.InvariantCulture),
            ["FlowMinutesPerRun"] = Num(FlowMinutesPerRun, "the minutes a flow run saves", 0, 240).ToString(CultureInfo.InvariantCulture),
            ["PowerBiCopilotMinutes"] = Num(PowerBiCopilotMinutes, "the minutes a Copilot in Power BI request saves", 0, 240).ToString(CultureInfo.InvariantCulture),
            ["CostIdleDays"] = SettingWriter.Int(CostIdleDays, "Unused after", 7, 180).ToString(CultureInfo.InvariantCulture),
            ["SpareSeatPercent"] = Num(string.IsNullOrWhiteSpace(SpareSeatPercent) ? "10" : SpareSeatPercent, "the spare seats' share", 0, 100).ToString(CultureInfo.InvariantCulture),
        }));
    });

    public Task<IActionResult> OnPostPriceAsync(string? item, string? price, string? tierFrom, string? tierPrice, string? dbGb, string? fileGb, string? spare, string? capacity) => AdminChangeAsync(async () =>
    {
        if (!decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) || p < 0 || p > 1000000) throw new ArgumentException("the price is a number, such as 15 or 0.01.");
        var name = await Db.ScalarAsync<string?>("SELECT Name FROM dash.CostPrice WHERE Item = @item;", new { item }) ?? throw new ArgumentException("no such price.");
        await Db.ExecuteAsync("UPDATE dash.CostPrice SET MonthlyPrice = @p, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME() WHERE Item = @item;", new { p, item, by = Viewer.Upn ?? Viewer.WindowsName });
        var also = "";
        /* 2.6.9: the volume price and the capacity a seat brings, where the form has them (a per-user license's row). */
        if (tierFrom is not null || tierPrice is not null || dbGb is not null || fileGb is not null || spare is not null || capacity is not null)
        {
            var (tf, tp, db, fl) = PriceExtras(tierFrom, tierPrice, dbGb, fileGb);
            var sp = string.IsNullOrWhiteSpace(spare) ? (decimal?)null
                : decimal.TryParse(spare, NumberStyles.Number, CultureInfo.InvariantCulture, out var s) && s >= 0 && s <= 100 ? s : throw new ArgumentException("the spare share is a percentage, 0 to 100.");
            var fc = capacity == "1";
            await Db.ExecuteAsync("UPDATE dash.CostPrice SET TierFromSeats = @tf, TierPrice = @tp, DatabaseGbPerSeat = @db, FileGbPerSeat = @fl, SparePercent = @sp, ForCapacity = @fc WHERE Item = @item;", new { tf, tp, db, fl, sp, fc, item });
            also = (tf is null ? ", no volume price" : $", {tp!.Value.ToString("0.####", CultureInfo.InvariantCulture)} from {tf:#,0} seats") +
                   (db is null && fl is null ? "" : $", {db ?? 0:0.###} GB database and {fl ?? 0:0.###} GB file a seat") +
                   (sp is null ? "" : $", {sp:0.##}% kept spare") + (fc ? ", bought for capacity or the volume price" : "");
        }
        return $"{name}: {p.ToString("0.####", CultureInfo.InvariantCulture)}{also}.";
    });

    /// <summary>The volume price and the Dataverse capacity a seat brings, from the price form (2.6.9): both of a tier or neither. Pure, for the tests.</summary>
    public static (int? TierFrom, decimal? TierPrice, decimal? DatabaseGb, decimal? FileGb) PriceExtras(string? tierFrom, string? tierPrice, string? dbGb, string? fileGb)
    {
        static decimal? Dec(string? v, string what, decimal max) => string.IsNullOrWhiteSpace(v) ? null
            : decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0 && d <= max ? d : throw new ArgumentException($"{what} is a number from 0 to {max}.");
        int? tf = string.IsNullOrWhiteSpace(tierFrom) ? null
            : int.TryParse(tierFrom, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= 10_000_000 ? n : throw new ArgumentException("the volume price's seats are a whole number.");
        var tp = Dec(tierPrice, "the volume price", 1_000_000);
        if ((tf is null) != (tp is null)) throw new ArgumentException("give both the seats the volume price starts from and the price, or neither.");
        return (tf, tp, Dec(dbGb, "the database GB a seat", 100), Dec(fileGb, "the file GB a seat", 1000));
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
            "On: every page, export, digest and API call that names people is refused, and Power BI's Users table shows a salted hash instead of names, sign-ins and managers, the same for the same person, so counts and org slicing still work. " +
            "Units, named after their managers, go by their department, and no report is narrowed to a manager's part of the organization. Aggregates keep the minimum group size (Admin › Access). " +
            "If the setting cannot be read, privacy mode counts as on until it can."));

        Panels.Add(new("Retention", new HtmlString(Form("retention",
            Input("AuditLogRetentionDays", $"Keep the dashboard's audit log (days; 0 keeps it; at least {MinAuditLogDays})", V("AuditLogRetentionDays"), 6, "number") +
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

    /// <summary>
    /// The shortest the audit log may be kept, in days (2.4.12): it is how administrators are held to account, so the person
    /// it records cannot cut it to a day and erase their own record. Before, any number from 0 was taken.
    /// </summary>
    public const int MinAuditLogDays = 365;

    /// <summary>Why these audit-log and JSON retentions cannot be saved, or null.</summary>
    public static string? RetentionProblem(int auditDays, int jsonDays) =>
        auditDays is > 0 and < MinAuditLogDays ? $"audit-log retention is 0 (keep it) or at least {MinAuditLogDays} days: the audit log is how administrators are held to account."
        : jsonDays is > 0 and < 200 ? "JSON retention is 0 (keep) or at least 200 days: past Purview's 180 days it cannot be fetched again."
        : null;

    public Task<IActionResult> OnPostRetentionAsync(string? AuditLogRetentionDays, string? NotificationRetentionDays, string? JsonRetentionDays) => AdminChangeAsync(async () =>
    {
        var json = SettingWriter.Int(JsonRetentionDays, "JSON retention", 0, 36500);
        var audit = SettingWriter.Int(AuditLogRetentionDays, "Audit-log retention", 0, 36500);
        if (RetentionProblem(audit, json) is { } bad) throw new ArgumentException(bad);
        /* With the old and new values, so the audit log says how long it was set to keep itself. */
        return SettingWriter.Saved(await SettingWriter.SaveAsync(Db, S.Settings, Viewer.Upn ?? Viewer.WindowsName, new Dictionary<string, string?>
        {
            ["AuditLogRetentionDays"] = audit.ToString(CultureInfo.InvariantCulture),
            ["NotificationRetentionDays"] = SettingWriter.Int(NotificationRetentionDays, "Notification-log retention", 0, 36500).ToString(CultureInfo.InvariantCulture),
            ["JsonRetentionDays"] = json.ToString(CultureInfo.InvariantCulture),
        }, showValues: true));
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

    /* Agent ids (column 6) are no longer tied to Admin › Reference data's agents (2.4.12): since 2.4.11 a Copilot Studio
       or Microsoft agent is known by the app identity that names it, which a record's agent id never equals, so a name
       given here reached none of them, and an id that did match could be another agent's. Agents are named in Reference data. */
    /// <summary>
    /// 2.7.1: the product name for each of the tenant's licenses, which every page shows in place of its part number
    /// (dash.SkuName, seeded with Microsoft's names for the common ones). A license with none is shown by its part number.
    /// </summary>
    async Task LicenseNamesAsync()
    {
        if (await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dash.SkuName', N'U') IS NULL THEN 0 ELSE 1 END") == 0) return;
        var hasSkus = await Db.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'audit.LicenseSku', N'U') IS NULL THEN 0 ELSE 1 END") == 1;
        var rows = await Db.QueryAsync<(string Part, string? Name, string? By, int InTenant)>($@"
WITH p AS ({(hasSkus ? "SELECT SkuPartNumber FROM audit.LicenseSku UNION " : "")}SELECT SkuPartNumber FROM dash.SkuName)
SELECT p.SkuPartNumber, n.ProductName, n.UpdatedBy, {(hasSkus ? "CASE WHEN EXISTS (SELECT 1 FROM audit.LicenseSku AS s WHERE s.SkuPartNumber = p.SkuPartNumber) THEN 1 ELSE 0 END" : "0")}
FROM p LEFT JOIN dash.SkuName AS n ON n.SkuPartNumber = p.SkuPartNumber
ORDER BY 4 DESC, CASE WHEN n.ProductName IS NULL THEN 0 ELSE 1 END, p.SkuPartNumber;");
        var shown = rows.Where(r => r.InTenant == 1).ToList();
        var t = new Table { Headers = ["Part number", "Product name", "Named by"], EmptyText = "The license tool has not read the tenant's licenses yet." };
        foreach (var r in shown)
            t.Rows.Add([r.Part, r.Name ?? "(none: shown by its part number)", r.Name is null ? "" : r.By == "install" ? "Microsoft's name" : r.By]);
        var form = Form("skuname", Input("part", "Part number", null, 30) + Input("productName", "Product name", null, 30) + Button("Save"));
        Panels.Add(new("License product names", new HtmlString(t.Render().Value + "<h3>Name a license</h3>" + form),
            $"The tenant's {shown.Count:#,0} licenses, by part number, and the product name the pages show for each; {shown.Count(r => r.Name is null):#,0} have none yet. " +
            "Microsoft's names for the common licenses come with the install; give one here, or change it, and it is kept on every install. An empty name removes it.", "full"));
    }

    public Task<IActionResult> OnPostSkunameAsync(string? part, string? productName) => AdminChangeAsync(async () =>
    {
        part = part?.Trim();
        if (string.IsNullOrEmpty(part) || part.Length > 100) throw new ArgumentException("give the license's part number, as License pool shows it.");
        var name = productName?.Trim();
        var was = await Db.ScalarAsync<string?>("SELECT ProductName FROM dash.SkuName WHERE SkuPartNumber = @part;", new { part });
        if (string.IsNullOrEmpty(name))
            return await Db.ExecuteAsync("DELETE FROM dash.SkuName WHERE SkuPartNumber = @part;", new { part }) > 0
                ? $"License name removed: {part} (was {was}); it is shown by its part number." : $"{part} had no name.";
        if (name.Length > 200) throw new ArgumentException("a product name is at most 200 characters.");
        await Db.ExecuteAsync(@"
MERGE dash.SkuName AS t USING (SELECT @part AS SkuPartNumber) AS s ON t.SkuPartNumber = s.SkuPartNumber
WHEN MATCHED THEN UPDATE SET ProductName = @name, UpdatedBy = @by, UpdatedUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (SkuPartNumber, ProductName, UpdatedBy) VALUES (@part, @name, @by);", new { part, name, by = Viewer.Upn ?? Viewer.WindowsName });
        return $"License named: {part} is \"{name}\"" + (was is not null && was != name ? $" (was \"{was}\")." : ".");
    });

    const string NameExpr = "COALESCE(n.Name, CASE WHEN v.ColumnId IN (11, 30) THEN sl.Name END)";
    const string Joins = @"LEFT JOIN dash.ValueName AS n ON n.ColumnId = v.ColumnId AND n.Value = v.Value
LEFT JOIN dash.SensitivityLabel AS sl ON v.ColumnId IN (11, 30) AND sl.LabelId = v.Value";

    protected override async Task BuildAsync()
    {
        ShowDone();
        await LicenseNamesAsync();
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
        /* The mapping file (2.4.5): every column's values and names in one CSV, the format the upload reads. */
        var mapping = $"<form class=\"admin\" method=\"post\" enctype=\"multipart/form-data\" action=\"{E(Request.Path + "?handler=upload" + (col is null ? "" : $"&col={col.ColumnId}") + TenantQuery)}\">{Token()}" +
                      "<label>Names from a CSV (column id, value, name, note)<input type=\"file\" name=\"file\" accept=\".csv,text/csv\"/></label>" + Button("Upload") + "</form>" +
                      $"<p class=\"sub\"><a href=\"{E(Request.Path + "?handler=download")}\">Download the mapping file</a>: every value of every column below, with its name and note. Fill in or change the names, and upload it back; an empty name removes one.</p>";
        Panels.Add(new("Columns", new HtmlString(mapping + ct.Render().Value), "Columns with no values yet are hidden. The catalog fills as records are flattened; for data flattened before it existed, run Invoke-CopilotMaintenance.ps1 -Task RebuildValueCatalog once."));
        if (col is null || col.Capped) return;

        var pg = new Paging(Request, new Dictionary<string, string> { ["most"] = "v.Records DESC", ["unnamed"] = $"CASE WHEN {NameExpr} IS NULL THEN 0 ELSE 1 END, v.Records DESC", ["value"] = "v.Value", ["new"] = "v.FirstSeenUtc DESC", ["last"] = "v.LastSeenUtc DESC, v.Value" }, "most");
        var vals = await Db.QueryAsync<ValRow>($@"
SELECT v.Value, v.Records, v.FirstSeenUtc, v.LastSeenUtc, {NameExpr} AS Name, n.Note, COUNT(*) OVER () AS TotalRows
FROM audit.ValueCatalog AS v {Joins}
WHERE v.ColumnId = @id AND (@q IS NULL OR v.Value LIKE @q OR {NameExpr} LIKE @q)
{pg.OrderAndPage()};", new { id = col.ColumnId, q = pg.Like });
        pg.Total = vals.FirstOrDefault()?.TotalRows ?? 0;
        var vt = new Table
        {
            Headers = ["Value", "Times seen", "First seen", "Last seen", "Name"], Numeric = new HashSet<int> { 1 }, EmptyText = "No values match.",
            Server = new(pg, Request, ["value", "most", "new", "last", null], new HashSet<int> { 1, 2, 3 }),
        };
        foreach (var v in vals)
            vt.Rows.Add([v.Value, v.Records, v.FirstSeenUtc.Date, v.LastSeenUtc.Date, new HtmlString(Form("name",
                $"<input type=\"hidden\" name=\"col\" value=\"{col.ColumnId}\"/><input type=\"hidden\" name=\"value\" value=\"{E(v.Value)}\"/>" +
                Input("name", "Name", v.Name, 26) + Input("note", "Note", v.Note, 20) + Button("Save", quiet: true), $"col={col.ColumnId}"))]);
        var upload = $"<p class=\"sub\"><a href=\"{E(Request.Path + $"?handler=download&col={col.ColumnId}")}\">Download the mapping file for this column</a>: its values and names, to fill in and upload back above.</p>";
        Panels.Add(new(col.Label, new HtmlString(upload + pg.Controls(Request, new Dictionary<string, string> { ["most"] = "Most seen", ["unnamed"] = "Unnamed first", ["value"] = "Value", ["new"] = "Newest" }).Value + vt.Render().Value),
            col.ColumnId is 11 or 30 ? "Label names are shared with Admin › Reference data, where their order is set."
                : col.ColumnId == 6 ? "Agents are named in Admin › Reference data, and that is the name every page, digest and alert shows. A name here labels this agent id in Power BI only. Clear a name to remove it."
                : "Clear a name to remove it."));
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
        return $"{Count(saved, "name")} saved, {cleared} left without a name." + (errors.Count == 0 ? "" : $" {Count(errors.Count, "line")} skipped: " + string.Join(" ", errors.Take(5)) + (errors.Count > 5 ? $" And {errors.Count - 5} more." : ""));
    });

    /// <summary>The mapping file: one column's values and names, or (no column) every column's but those with too many to name.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(short? col)
    {
        if (!await PrepareAsync()) return Page();
        var vals = await Db.QueryAsync<(short ColumnId, string Value, string? Name, string? Note)>($@"
SELECT v.ColumnId, v.Value, {NameExpr}, n.Note
FROM audit.ValueCatalog AS v {Joins}
JOIN audit.ValueCatalogColumn AS c ON c.ColumnId = v.ColumnId AND c.Capped = 0
WHERE @col IS NULL OR v.ColumnId = @col
ORDER BY v.ColumnId, v.Records DESC;", new { col });
        var t = new Table { Headers = ["ColumnId", "Value", "Name", "Note"] };
        foreach (var v in vals) t.Rows.Add([(int)v.ColumnId, v.Value, v.Name, v.Note]);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, null, $"value names mapping file{(col is null ? "" : $", column {col}")}: {vals.Count} values");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(t.ToCsv())).ToArray(), "text/csv", col is null ? "value-names-mapping.csv" : $"value-names-mapping-{col}.csv");
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
        var t = new Table { Zone = Zone, Headers = ["Session", "Date", "Attendees", "Imported (UTC)", ""], Numeric = new HashSet<int> { 2 }, EmptyText = "No training records yet." };
        foreach (var x in sessions)
            t.Rows.Add([x.Session, x.AttendedOn, x.People, x.Imported, new HtmlString(Form("remove",
                $"<input type=\"hidden\" name=\"session\" value=\"{E(x.Session)}\"/><input type=\"hidden\" name=\"date\" value=\"{x.AttendedOn:yyyy-MM-dd}\"/>" +
                Button("Remove", $"Remove the {x.People} attendance records of {x.Session} on {x.AttendedOn:yyyy-MM-dd}?", quiet: true)))]);
        var upload = $"<form class=\"admin\" method=\"post\" enctype=\"multipart/form-data\" action=\"{E(Request.Path + "?handler=upload" + TenantQuery)}\">{Token()}" +
                     "<label>Attendance CSV: UPN, session, date (yyyy-MM-dd)<input type=\"file\" name=\"file\" accept=\".csv,text/csv\"/></label>" + Button("Upload") + "</form>" +
                     $"<p class=\"sub\"><a href=\"{E(Request.Path + "?handler=mapping")}\">Download the mapping file</a>: every attendance record held, in the same columns, to add to and upload back. " +
                     $"Or <a href=\"{E(Request.Path + "?handler=template")}\">download an empty template</a>. A header row is fine; a record already there is skipped.</p>";
        Panels.Add(new("Sessions", new HtmlString(upload + t.Render().Value), "The effect is measured 28 days after each session, so a session shows on Enablement › Training effect four weeks after it."));
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
        return $"{Count(added, "attendance record")} added from {Count(rows.Count, "line")}." + (errors.Count == 0 ? "" : $" {Count(errors.Count, "line")} skipped: " + string.Join(" ", errors.Take(5)) + (errors.Count > 5 ? $" And {errors.Count - 5} more." : ""));
    });

    public Task<IActionResult> OnPostRemoveAsync(string? session, DateTime? date) => AdminChangeAsync(async () =>
    {
        var n = await Db.ExecuteAsync("DELETE FROM dash.TrainingAttendance WHERE Session = @session AND AttendedOn = @date", new { session, date });
        return $"{Count(n, "attendance record")} of {session} removed.";
    });

    /// <summary>The mapping file (2.4.5): every attendance record held, as UPN, Session, Date, which the upload reads back.</summary>
    public async Task<IActionResult> OnGetMappingAsync()
    {
        if (!await PrepareAsync()) return Page();
        var rows = await Db.QueryAsync<(string Upn, string Session, DateTime AttendedOn)>(
            "SELECT Upn, Session, AttendedOn FROM dash.TrainingAttendance ORDER BY AttendedOn DESC, Session, Upn;");
        var t = new Table { Headers = ["UPN", "Session", "Date"] };
        foreach (var r in rows) t.Rows.Add([r.Upn, r.Session, r.AttendedOn.ToString("yyyy-MM-dd")]);
        await S.Audit.WriteAsync(Viewer, "Export", Request.Path, null, $"training attendance mapping file: {rows.Count} records");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(t.ToCsv())).ToArray(), "text/csv", "training-attendance-mapping.csv");
    }

    public async Task<IActionResult> OnGetTemplateAsync()
    {
        if (!await PrepareAsync()) return Page();
        return File(Encoding.UTF8.GetBytes("UPN,Session,Date\r\nada.lovelace@contoso.com,Copilot in Outlook,2026-10-01\r\n"), "text/csv", "training-attendance-template.csv");
    }
}
