using System.Data;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;

namespace CopilotInsights.Infrastructure;

/// <summary>Passwords and client secrets, encrypted in the database (dash.Secret) and read only through its procedures.</summary>
public sealed class SecretStore(Db db)
{
    public const string SmtpPassword = "SmtpPassword", ExoClientSecret = "ExoClientSecret", AlertWebhookUrl = "AlertWebhookUrl";

    /// <summary>Where the stored password may be sent: <see cref="MailConfig.PasswordTarget"/> when it was entered.</summary>
    public const string SmtpPasswordFor = "SmtpPasswordFor";

    public async Task<string?> GetAsync(string name)
    {
        await using var c = db.Open();
        var p = new DynamicParameters();
        p.Add("@Name", name, DbType.AnsiString, size: 64);
        p.Add("@Value", dbType: DbType.String, size: 2000, direction: ParameterDirection.Output);
        await c.ExecuteAsync("dash.usp_GetSecret", p, commandType: CommandType.StoredProcedure);
        return p.Get<string?>("@Value");
    }

    /// <summary>
    /// Stores the secret; null or empty removes it. A secret SQL Server cannot encrypt here is refused in words:
    /// on a database restored to another instance its keys cannot be opened, and the save ended on the error page,
    /// unaudited. The installer makes the keys again (Create-CopilotOperationsSchema.sql).
    /// </summary>
    public async Task SetAsync(string name, string? value, string by)
    {
        try { await db.ExecuteAsync("EXEC dash.usp_SetSecret @Name = @name, @Value = @value, @By = @by;", new { name, value, by }); }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 15581 or 15329 or 15466 or 15313)   // a key that cannot be opened
        {
            throw new ArgumentException("it was not stored: SQL Server could not encrypt it (" + ex.Message.TrimEnd('.') + "). " +
                "If the database was restored to another SQL Server instance, run the installer again, which makes its keys again, then enter it here again.", ex);
        }
    }
}

public enum MailMode { Off, ExchangeOnline, Smtp }

/// <summary>How email is sent, from dash.Setting and dash.Secret. Pure, so its checks can be tested.</summary>
public sealed record MailConfig
{
    public MailMode Mode { get; init; }
    public string? From { get; init; }
    public string? ReplyTo { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    /// <summary>StartTls, Implicit, Auto or None.</summary>
    public string Security { get; init; } = "StartTls";
    public string? User { get; init; }
    public string? Password { get; init; }
    /// <summary>The <see cref="PasswordTarget"/> the stored password was entered for; null for one stored before 2.4.12.</summary>
    public string? PasswordFor { get; init; }
    public bool AcceptAnyCertificate { get; init; }
    /// <summary>Exchange Online: Password (SMTP AUTH) or OAuth (client credentials, SMTP.SendAsApp).</summary>
    public string ExoAuth { get; init; } = "Password";
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public const string ExchangeOnlineHost = "smtp.office365.com";
    public static readonly string[] SecurityChoices = ["StartTls", "Implicit", "Auto", "None"];

    public static MailConfig Load(IReadOnlyDictionary<string, string?> s, string? password, string? clientSecret, string? passwordFor = null)
    {
        string? V(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        return new MailConfig
        {
            Mode = Enum.TryParse<MailMode>(V("MailMode"), true, out var m) ? m : MailMode.Off,
            From = V("MailFrom"), ReplyTo = V("MailReplyTo"), Host = V("SmtpHost"),
            Port = int.TryParse(V("SmtpPort"), out var port) ? port : 587,
            Security = V("SmtpSecurity") is { } sec && SecurityChoices.Contains(sec, StringComparer.OrdinalIgnoreCase) ? SecurityChoices.First(x => x.Equals(sec, StringComparison.OrdinalIgnoreCase)) : "StartTls",
            User = V("SmtpUser"), Password = password, PasswordFor = passwordFor, AcceptAnyCertificate = V("SmtpAcceptAnyCertificate") == "1",
            ExoAuth = V("ExoAuth") is "OAuth" ? "OAuth" : "Password",
            TenantId = V("ExoTenantId"), ClientId = V("ExoClientId"), ClientSecret = clientSecret,
        };
    }

    /// <summary>
    /// Where a stored password goes and as whom: the way of sending, the server, port and security, whether the
    /// server's certificate is checked, and the user name. The password is sent only while this is what it was entered for.
    /// Before, an administrator could point the site at a server of their own, leave the password field empty, and have
    /// the site sign in there with the stored mailbox or relay password, in clear text with security None.
    /// </summary>
    public string PasswordTarget()
    {
        var (host, port, tls) = Endpoint();
        var user = Mode == MailMode.ExchangeOnline ? User ?? From : User;
        return string.Join("|", Mode, host.ToLowerInvariant(), port.ToString(System.Globalization.CultureInfo.InvariantCulture), tls, user?.ToLowerInvariant() ?? "", AcceptAnyCertificate ? "any certificate" : "checked");
    }

    /// <summary>True when the stored password was entered for another server, port, security or user than these.</summary>
    public bool PasswordMoved => !string.IsNullOrEmpty(Password) && PasswordFor is not null && PasswordFor != PasswordTarget();

    /// <summary>The server, port and TLS actually used: Exchange Online is always smtp.office365.com:587 with STARTTLS.</summary>
    public (string Host, int Port, SecureSocketOptions Tls) Endpoint() => Mode == MailMode.ExchangeOnline
        ? (ExchangeOnlineHost, 587, SecureSocketOptions.StartTls)
        : (Host ?? "", Port, Security switch
        {
            "Implicit" => SecureSocketOptions.SslOnConnect,
            "Auto" => SecureSocketOptions.Auto,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        });

    /// <summary>What stops mail being sent, in words an administrator can act on. Empty when it can send.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (Mode == MailMode.Off) { p.Add("Email is off."); return p; }
        if (From is null || !MailboxAddress.TryParse(From, out _)) p.Add("Set the From address.");
        if (PasswordMoved && (Mode == MailMode.Smtp ? User is not null : ExoAuth != "OAuth"))
            p.Add("The server, port, security or user name changed after the password was stored: type the password again in Admin › Email.");
        if (ReplyTo is not null && !MailboxAddress.TryParse(ReplyTo, out _)) p.Add("The Reply-To address is not an email address.");
        if (Mode == MailMode.Smtp)
        {
            if (Host is null) p.Add("Set the SMTP server.");
            if (Port is < 1 or > 65535) p.Add("The SMTP port is 1 to 65535.");
            if (User is not null && string.IsNullOrEmpty(Password)) p.Add("A user name is set but no password is stored.");
        }
        else if (ExoAuth == "OAuth")
        {
            if (TenantId is null) p.Add("Set the tenant for Exchange Online OAuth.");
            if (ClientId is null || !Guid.TryParse(ClientId, out _)) p.Add("Set the app (client) id for Exchange Online OAuth.");
            if (string.IsNullOrEmpty(ClientSecret)) p.Add("Store the app's client secret.");
        }
        else
        {
            if (string.IsNullOrEmpty(Password)) p.Add("Store the mailbox's password (Exchange Online SMTP AUTH).");
        }
        return p;
    }

    /// <summary>Addresses from a setting: separated by ; or , and each one checked.</summary>
    public static IReadOnlyList<MailboxAddress> Recipients(string? list)
    {
        var r = new List<MailboxAddress>();
        foreach (var part in (list ?? "").Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MailboxAddress.TryParse(part, out var a) && a.Address.Contains('@')) r.Add(a);
        return r;
    }
}

/// <summary>Sends email by SMTP: Exchange Online (password or OAuth) or any server, on any port. Every send is logged in dash.NotificationLog.</summary>
public sealed class Mailer(Db db, Settings settings, SecretStore secrets, IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<Mailer> log)
{
    public async Task<MailConfig> ConfigAsync()
    {
        var s = await settings.AllAsync();
        var mode = s.GetValueOrDefault("MailMode");
        if (mode is null || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) return MailConfig.Load(s, null, null);
        return MailConfig.Load(s, await secrets.GetAsync(SecretStore.SmtpPassword), await secrets.GetAsync(SecretStore.ExoClientSecret), await secrets.GetAsync(SecretStore.SmtpPasswordFor));
    }

    /// <summary>Sends one message and logs it. Returns null when sent, else why not.</summary>
    public async Task<string?> SendAsync(string kind, IReadOnlyList<MailboxAddress> to, string subject, string html, string text, CancellationToken ct = default) =>
        (await DeliverAsync(kind, to, subject, html, text, ct)).Error;

    /// <summary>A status that sending again will not change: email is off, or the alert has no recipients.</summary>
    public static bool Settled(string status) => status is "Sent" or "NoRecipients" or "MailOff";

    /// <summary>
    /// The same, with the status logged (Sent, Failed, NoRecipients or MailOff), so an alert can tell a failure worth
    /// another try from one that is not. A stop that cancels the send leaves it unsent: once the message is on
    /// its way it is no longer cancelled (SendCoreAsync).
    /// </summary>
    public Task<(string Status, string? Error)> DeliverAsync(string kind, IReadOnlyList<MailboxAddress> to, string subject, string html, string text, CancellationToken ct = default) =>
        DeliverAsync(kind, to, subject, html, text, null, ct);

    /// <summary>
    /// The same, with images the HTML shows inline (cid:<i>id</i>), as a report subscription's charts: carried in the
    /// message, so a mail app shows them without fetching anything.
    /// </summary>
    public async Task<(string Status, string? Error)> DeliverAsync(string kind, IReadOnlyList<MailboxAddress> to, string subject, string html, string text,
        IReadOnlyList<(string Id, byte[] Png)>? images, CancellationToken ct = default)
    {
        string? error;
        string status;
        string? detail = null;
        if (to.Count == 0) { status = "NoRecipients"; error = "No recipients are set."; }
        else
        {
            var cfg = await ConfigAsync();
            var problems = cfg.Problems();
            if (problems.Count > 0) { status = cfg.Mode == MailMode.Off ? "MailOff" : "Failed"; error = string.Join(" ", problems); }
            else
            {
                /* What is said to the mail server and what it answers, kept with a test message and with any
                   message that is not sent: the place to look when email does not arrive (Admin › Email). */
                using var said = new MemoryStream();
                try
                {
                    await SendCoreAsync(cfg, to, subject, html, text, images, ct, said); status = "Sent"; error = null;
                    if (kind == "Test") detail = SmtpTranscript.Compose(cfg, Said(said), null);
                }
                /* A timeout is an OperationCanceledException as well: it is a failed send, not the site stopping. */
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    status = "Failed"; error = ex is OperationCanceledException ? "The mail server or Entra did not answer in time." : Describe(ex);
                    detail = SmtpTranscript.Compose(cfg, Said(said), ex);
                    log.LogWarning(ex, "Email '{Subject}' was not sent", subject);
                }
            }
        }
        var row = new { kind, subject = Cut(subject, 400), to = Cut(string.Join("; ", to.Select(a => a.Address)), 2000), status, error = error is null ? null : Cut(error, 2000), detail };
        try
        {
            try { await db.ExecuteAsync("INSERT INTO dash.NotificationLog (Kind, Subject, Recipients, Status, Error, Detail) VALUES (@kind, @subject, @to, @status, @error, @detail);", row); }
            /* A database from before the Detail column (207: invalid column name): the row as it always was. */
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
            {
                await db.ExecuteAsync("INSERT INTO dash.NotificationLog (Kind, Subject, Recipients, Status, Error) VALUES (@kind, @subject, @to, @status, @error);", row);
            }
        }
        catch (Exception ex) { log.LogError(ex, "Could not write dash.NotificationLog"); }
        return (status, error);
    }

    /// <summary>The protocol log so far, as text; empty when nothing was said (the server was never reached).</summary>
    static string Said(MemoryStream said) => System.Text.Encoding.UTF8.GetString(said.GetBuffer(), 0, (int)said.Length);

    async Task SendCoreAsync(MailConfig cfg, IReadOnlyList<MailboxAddress> to, string subject, string html, string text, IReadOnlyList<(string Id, byte[] Png)>? images, CancellationToken ct, Stream said)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(cfg.From!));
        if (cfg.ReplyTo is not null) msg.ReplyTo.Add(MailboxAddress.Parse(cfg.ReplyTo));
        foreach (var a in to) msg.To.Add(a);
        msg.Subject = subject;
        var body = new BodyBuilder { HtmlBody = html, TextBody = text };
        foreach (var (id, png) in images ?? [])
        {
            var part = body.LinkedResources.Add(id + ".png", png, new ContentType("image", "png"));
            part.ContentId = id;
        }
        msg.Body = body.ToMessageBody();

        /* The library writes each line of the conversation to the stream, with what it knows to be a secret already hidden. */
        using var client = new SmtpClient(new MailKit.ProtocolLogger(said, leaveOpen: true)) { Timeout = 60_000 };
        if (cfg.AcceptAnyCertificate) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var (host, port, tls) = cfg.Endpoint();
        await client.ConnectAsync(host, port, tls, ct);
        if (cfg.Mode == MailMode.ExchangeOnline && cfg.ExoAuth == "OAuth")
            await client.AuthenticateAsync(new SaslMechanismOAuth2(cfg.From!, await TokenAsync(cfg, ct)), ct);
        else if (cfg.Mode == MailMode.ExchangeOnline)
            await client.AuthenticateAsync(cfg.User ?? cfg.From!, cfg.Password!, ct);
        else if (cfg.User is not null)
            await client.AuthenticateAsync(cfg.User, cfg.Password!, ct);
        /* Once the message is on its way it is not cancelled: a stop part-way could not tell whether the server
           took it, and a manager digest cut off there was neither sent nor tried again. A stop before this point leaves the
           message unsent, for certain; the client's own timeout still bounds what follows. */
        await client.SendAsync(msg, CancellationToken.None);
        await client.DisconnectAsync(true, CancellationToken.None);
    }

    sealed record TokenReply([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    /// <summary>
    /// Where a token is kept: under the app and a hash of the secret that got it, so it answers only a
    /// configuration holding that same secret. Dashboard tenants that share one app and secret share the token. Before,
    /// the key was the tenant and app alone: an administrator of another dashboard tenant who typed in this one's tenant
    /// and app id, with any secret, was handed its token, and a mistyped new secret passed Send a test for an hour.
    /// </summary>
    public static string TokenKey(MailConfig cfg) =>
        $"exo-token:{cfg.TenantId}:{cfg.ClientId}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cfg.ClientSecret ?? "")))}";

    /// <summary>An app-only token for Exchange Online SMTP (scope outlook.office365.com), cached until shortly before it expires.</summary>
    async Task<string> TokenAsync(MailConfig cfg, CancellationToken ct)
    {
        var key = TokenKey(cfg);
        if (cache.TryGetValue(key, out string? t) && t is not null) return t;
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var r = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(cfg.TenantId!)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = cfg.ClientId!, ["client_secret"] = cfg.ClientSecret!,
                ["scope"] = "https://outlook.office365.com/.default", ["grant_type"] = "client_credentials",
            }), ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"Entra refused the token for Exchange Online ({(int)r.StatusCode}): {Cut(await r.Content.ReadAsStringAsync(ct), 300)}");
        var reply = await r.Content.ReadFromJsonAsync<TokenReply>(ct) ?? throw new InvalidOperationException("Entra returned no token.");
        cache.Set(key, reply.AccessToken, TimeSpan.FromSeconds(Math.Max(60, reply.ExpiresIn - 300)));
        return reply.AccessToken;
    }

    static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Why a send failed, in words: the refusal itself rather than "see inner exception".</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AuthenticationException) return "The server refused the user name or password (" + ex.Message.TrimEnd('.') + ").";
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            if (!e.Message.Contains("see inner exception", StringComparison.OrdinalIgnoreCase) && !parts.Contains(e.Message)) parts.Add(e.Message.Trim());
        var text = parts.Count == 0 ? ex.GetType().Name : string.Join(" ", parts);
        return ex is IOException or System.Net.Sockets.SocketException && !text.Contains("refused", StringComparison.OrdinalIgnoreCase)
            ? text + " (the server closed the connection: often a wrong password, or TLS the server does not expect)"
            : text;
    }
}
