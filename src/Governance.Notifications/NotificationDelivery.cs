using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;
using Governance.Notifications.Templates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Governance.Notifications;

/// <summary>
/// Delivers N01..N22 (section 8). Action notices go out at once by email, Teams activity feed and team channel as
/// the rule says; informed parties get a digest line. One delivery per event, recipient and channel (de-duplicated
/// in ntf.Delivery), so a retried outbox row never notifies anyone twice. In Record mode nothing leaves the server.
/// </summary>
public sealed class NotificationDelivery(
    Db db,
    RecipientResolver recipients,
    EmailRenderer renderer,
    TextBlocks texts,
    IMailSender mail,
    ITeamsActivityBulkSender activity,
    IChannelPoster channels,
    BusinessCalendarProvider calendars,
    OutboxWriter outbox,
    IClock clock,
    IOptions<DispatchOptions> options,
    DeliveryLedger ledger,
    ILogger<NotificationDelivery> log) : IOutboxHandler
{
    private static int _channelAuthFailures;

    public bool CanHandle(string eventType) => eventType.Length == 3 && eventType[0] == 'N' && char.IsDigit(eventType[1]);

    public async Task HandleAsync(OutboxRow row, OutboxMessage m, CancellationToken ct)
    {
        var record = options.Value.IsRecord;
        var (rule, list, calendar, header, footer, guidance) = await db.InTransactionAsync(async u =>
        {
            var r = await u.QuerySingleOrDefaultAsync<(string Code, string Channels, bool IsAction, bool IsEnabled)>(
                "SELECT Code, Channels, IsAction, IsEnabled FROM ntf.NotificationRule WHERE Code = @EventType", new { m.EventType });
            var people = await recipients.ResolveAsync(u, m);
            var cal = await calendars.GetAsync(u);
            return (r, people, cal, await texts.GetAsync(u, "email.header"), await texts.GetAsync(u, "email.footer"), await texts.GetAsync(u, $"email.guidance.{m.EventType}"));
        }, ct);
        if (rule.Code is null || !rule.IsEnabled) return;

        // Entra already emails approvers and reviewers; PPAC and the admin center send threshold alerts (section 8.2).
        if (m.Facts.TryGetValue("origin", out var origin) && origin is "ENTRA" or "MICROSOFT")
        {
            foreach (var p in list) await RecordAsync(row, p.PersonId, null, "EMAIL", "SUPPRESSED", m.Subject, "Microsoft already notifies this person.");
            return;
        }

        // Informed: a digest line instead of a notice.
        if (m.DigestOnly || !rule.IsAction)
        {
            await db.InTransactionAsync(async u =>
            {
                foreach (var p in list) await outbox.AddDigestAsync(u, p.PersonId, m.EventType, $"{m.Subject}: {m.Text}", m.LinkPath);
            }, ct);
            return;
        }

        var channelsOfRule = rule.Channels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var link = m.LinkPath is null ? null : options.Value.WorkbenchBaseUrl.TrimEnd('/') + m.LinkPath;
        var failures = new List<string>();
        var pendingElsewhere = false;

        // Every send is reserved first (PENDING row, unique per event, recipient and channel); the reservation also
        // renews and checks this node's lease, so two nodes never send the same notice (LeaseLostException stops us).
        async Task<bool> MayAsync(int? personId, string? address, string channel)
        {
            switch (await ledger.ReserveAsync(row, personId, address, channel, m.Subject, ct))
            {
                case Reservation.Reserved: return true;
                case Reservation.PendingElsewhere: pendingElsewhere = true; return false;
                default: return false;
            }
        }

        if (channelsOfRule.Contains("EMAIL"))
        {
            var facts = Facts(m, calendar);
            var email = await renderer.NoticeAsync($"[Workbench] {m.Subject}", header, KindLabel(m.EventType), m.Subject, m.Text, facts, link,
                string.IsNullOrEmpty(guidance) ? null : guidance, footer);
            foreach (var p in list)
            {
                if (!await MayAsync(p.PersonId, p.Address, "EMAIL")) continue;
                if (string.IsNullOrEmpty(p.Address)) { await RecordAsync(row, p.PersonId, null, "EMAIL", "SKIPPED", m.Subject, "No email address."); continue; }
                try
                {
                    if (!record) await mail.SendAsync(new MailMessageData(new[] { p.Address }, email.Subject, email.Html, email.Text, row.DedupeKey), ct);
                    await RecordAsync(row, p.PersonId, p.Address, "EMAIL", record ? "RECORDED" : "SENT", email.Subject, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add("email");
                    await RecordAsync(row, p.PersonId, p.Address, "EMAIL", "FAILED", email.Subject, Describe(ex));
                }
            }
        }

        if (channelsOfRule.Contains("ACTIVITY") && m.ActivityType is not null)
        {
            var targets = new List<RecipientResolver.Recipient>();
            foreach (var p in list)
            {
                if (!await MayAsync(p.PersonId, null, "ACTIVITY")) continue;
                if (p.EntraObjectId is null || !p.ActivityFeed) { await RecordAsync(row, p.PersonId, null, "ACTIVITY", "SKIPPED", m.Subject, "No Teams identity or activity feed turned off."); continue; }
                if (InQuietHours(p, calendar)) { await RecordAsync(row, p.PersonId, null, "ACTIVITY", "QUIET", m.Subject, "Quiet hours: email remains the notice of record."); continue; }
                targets.Add(p);
            }
            if (targets.Count > 0)
            {
                try
                {
                    if (!record)
                        await activity.SendBulkAsync(targets.Select(t => t.EntraObjectId!.Value).ToList(), m.ActivityType, m.Subject, m.LinkPath ?? "/Start", TemplateParameters(m), ct);
                    foreach (var t in targets) await RecordAsync(row, t.PersonId, null, "ACTIVITY", record ? "RECORDED" : "SENT", m.Subject, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Email stays the notice of record (X10): a failed activity notice is logged, not retried forever.
                    foreach (var t in targets) await RecordAsync(row, t.PersonId, null, "ACTIVITY", "FAILED", m.Subject, Describe(ex));
                    log.LogWarning("Teams activity notice for {Event} failed: {Type}", m.EventType, ex.GetType().Name);
                }
            }
        }

        if (channelsOfRule.Contains("CHANNEL"))
            foreach (var queue in m.Queues.Distinct())
            {
                if (!await MayAsync(null, queue, "CHANNEL:" + queue)) continue;
                if (!channels.IsConfigured) { await RecordAsync(row, null, queue, "CHANNEL:" + queue, "SKIPPED", m.Subject, "The Teams channel flow is not configured."); continue; }
                if (record) { await RecordAsync(row, null, queue, "CHANNEL:" + queue, "RECORDED", m.Subject, null); continue; }
                try
                {
                    await channels.PostAsync(queue, m.Subject, m.Text, link ?? options.Value.WorkbenchBaseUrl, ct);
                    Interlocked.Exchange(ref _channelAuthFailures, 0);
                    await RecordAsync(row, null, queue, "CHANNEL:" + queue, "SENT", m.Subject, null);
                }
                catch (PermanentDeliveryException ex)
                {
                    await RecordAsync(row, null, queue, "CHANNEL:" + queue, "DEAD_LETTER", m.Subject, ex.Message);
                    await AlertAsync($"N19:channel-queue:{queue}", $"Teams channel queue {queue} is unknown to the flow", ex.Message);
                }
                catch (IntegrationPermissionException ex)
                {
                    await RecordAsync(row, null, queue, "CHANNEL:" + queue, "FAILED", m.Subject, ex.Message);
                    if (Interlocked.Increment(ref _channelAuthFailures) >= 2)
                        await AlertAsync($"N19:channel-auth:{clock.UtcNow:yyyyMMdd}", "The Teams channel flow refused the Worker twice in a row", ex.Message);
                    failures.Add("channel");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RecordAsync(row, null, queue, "CHANNEL:" + queue, "FAILED", m.Subject, Describe(ex));
                    failures.Add("channel");
                }
            }

        if (failures.Count > 0) throw new InvalidOperationException($"Delivery failed for: {string.Join(", ", failures.Distinct())}. Delivered recipients are not notified again on retry.");
        // Another node reserved some deliveries a moment ago: retry the row later so a stopped node's work is not lost.
        if (pendingElsewhere) throw new InvalidOperationException("Some deliveries are in progress on another node; the row is retried later.");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Facts(OutboxMessage m, Application.Sla.BusinessCalendar calendar)
    {
        var facts = new List<KeyValuePair<string, string>>();
        if (m.Facts.TryGetValue("stage", out var stage) && !string.IsNullOrEmpty(stage)) facts.Add(new("Stage", stage));
        if (m.Facts.TryGetValue("due", out var due) && DateTime.TryParse(due, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            facts.Add(new("Due", calendar.FormatLocal(d.ToUniversalTime(), "ddd d MMM yyyy, HH:mm") + $" ({calendar.Zone.StandardName})"));
        foreach (var (k, v) in m.Facts.Where(f => f.Key is not ("stage" or "due" or "origin")))
            facts.Add(new(char.ToUpperInvariant(k[0]) + k[1..], v));
        return facts;
    }

    /// <summary>
    /// Exactly the parameters each activity type's templateText needs (setup/New-TeamsAppPackage.ps1), never {actor}:
    /// that token is reserved and Teams fills it with the app's name.
    /// </summary>
    public static Dictionary<string, string> TemplateParameters(OutboxMessage m)
    {
        var number = m.Subject.Split(':')[0].Trim();
        return m.ActivityType switch
        {
            "taskAssigned" => new() { ["taskTitle"] = m.Subject },
            "requestReturned" => new() { ["requestNumber"] = number },
            "decisionRecorded" => new() { ["requestNumber"] = number, ["outcome"] = m.Subject.Contains(':') ? m.Subject[(m.Subject.IndexOf(':') + 1)..].Trim() : m.Subject },
            "verificationFailed" => new() { ["requestNumber"] = number },
            "recertificationDue" => new() { ["workloadName"] = m.Subject },
            _ => new(),
        };
    }

    private static bool InQuietHours(RecipientResolver.Recipient p, Application.Sla.BusinessCalendar calendar)
    {
        if (p.QuietStart is not TimeSpan s || p.QuietEnd is not TimeSpan e) return false;
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, calendar.Zone).TimeOfDay;
        return s <= e ? now >= s && now < e : now >= s || now < e;
    }

    public static string KindLabel(string code) => code switch
    {
        "N06" => "Decision recorded",
        "N08" => "Action needed: verification",
        "N10" or "N11" or "N15" => "Finding",
        "N12" => "Consumption event",
        "N13" => "Exception expiry",
        "N14" => "Recertification",
        "N18" => "Commercial date",
        "N19" or "N21" => "Integration",
        "N20" => "Role vacancy",
        "N01" => "Request received",
        _ => "Action needed",
    };

    private Task RecordAsync(OutboxRow row, int? personId, string? address, string channel, string status, string subject, string? detail)
        => ledger.RecordAsync(row, personId, address, channel, status, subject, detail);

    private Task AlertAsync(string dedupe, string subject, string text)
        => db.InTransactionAsync(u => outbox.EnqueueAsync(u, new OutboxMessage
        {
            EventType = "N19", Roles = { new RoleTarget(Roles.WorkbenchAdmin, ScopeTypes.Tenant, null), new RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
            Subject = subject, Text = text, LinkPath = "/Admin/Integrations",
        }, dedupe));

    private static string Describe(Exception ex) => ex is IntegrationPermissionException or PermanentDeliveryException ? ex.Message : $"{ex.GetType().Name}: {(ex.Message.Length > 300 ? ex.Message[..300] : ex.Message)}";
}

/// <summary>N17: one email per person a day with their informed items (07:00 institution time by default, ntf.Preference.DigestHour).</summary>
public sealed class DigestBuilder(Db db, EmailRenderer renderer, TextBlocks texts, IMailSender mail, BusinessCalendarProvider calendars, IClock clock, IOptions<DispatchOptions> options)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var record = options.Value.IsRecord;
        var (calendar, header, footer, title, intro) = await db.InTransactionAsync(async u => (await calendars.GetAsync(u),
            await texts.GetAsync(u, "email.header"), await texts.GetAsync(u, "email.footer"), await texts.GetAsync(u, "email.digest.title"), await texts.GetAsync(u, "email.digest.intro")), ct);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, calendar.Zone);
        var people = await db.InTransactionAsync(u => u.QueryAsync<(int PersonId, string? Address, int DigestHour)>(
            """
            SELECT p.PersonId, COALESCE(p.Mail, CASE WHEN p.UserPrincipalName LIKE '%@%' THEN p.UserPrincipalName END) AS Address, COALESCE(pr.DigestHour, 7) AS DigestHour
            FROM sec.Person p LEFT JOIN ntf.Preference pr ON pr.PersonId = p.PersonId
            WHERE p.AccountEnabled = 1 AND EXISTS (SELECT 1 FROM ntf.DigestEntry d WHERE d.PersonId = p.PersonId AND d.SentUtc IS NULL)
            """), ct);
        var sent = 0;
        foreach (var p in people)
        {
            if (localNow.Hour < p.DigestHour) continue;
            var key = $"N17|{p.PersonId}|{localNow:yyyyMMdd}|EMAIL";
            var already = await db.InTransactionAsync(u => u.ScalarAsync<int>("SELECT COUNT(*) FROM ntf.Delivery WHERE DedupeKey = @key", new { key }), ct);
            if (already > 0) continue;
            var entries = (await db.InTransactionAsync(u => u.QueryAsync<(long Id, string Text, string? Link)>(
                "SELECT TOP (200) DigestEntryId, Text, Link FROM ntf.DigestEntry WHERE PersonId = @PersonId AND SentUtc IS NULL ORDER BY DigestEntryId", new { p.PersonId }), ct)).ToList();
            if (entries.Count == 0) continue;
            var items = entries.Select(e => new DigestEmail.Item(e.Text, e.Link is null ? null : options.Value.WorkbenchBaseUrl.TrimEnd('/') + e.Link)).ToList();
            var email = await renderer.DigestAsync(header, $"{title}: {entries.Count} item{(entries.Count == 1 ? "" : "s")}", intro, items, footer);
            string status = "SKIPPED";
            if (!string.IsNullOrEmpty(p.Address))
            {
                if (!record) await mail.SendAsync(new MailMessageData(new[] { p.Address! }, $"[Workbench] {email.Subject}", email.Html, email.Text, key), ct);
                status = record ? "RECORDED" : "SENT";
            }
            await db.InTransactionAsync(async u =>
            {
                await u.ExecuteAsync("UPDATE ntf.DigestEntry SET SentUtc = SYSUTCDATETIME() WHERE DigestEntryId IN (SELECT value FROM OPENJSON(@ids))",
                    new { ids = System.Text.Json.JsonSerializer.Serialize(entries.Select(e => e.Id)) });
                await u.ExecuteAsync("INSERT ntf.Delivery (RecipientPersonId, RecipientAddress, Channel, DedupeKey, Subject, Status) VALUES (@PersonId, @Address, 'EMAIL', @key, @subject, @status)",
                    new { p.PersonId, p.Address, key, subject = email.Subject, status });
            }, ct);
            sent++;
        }
        return sent;
    }
}
