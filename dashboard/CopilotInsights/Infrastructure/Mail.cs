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
    public const string SmtpPassword = "SmtpPassword", ExoClientSecret = "ExoClientSecret";

    public async Task<string?> GetAsync(string name)
    {
        await using var c = db.Open();
        var p = new DynamicParameters();
        p.Add("@Name", name, DbType.AnsiString, size: 64);
        p.Add("@Value", dbType: DbType.String, size: 2000, direction: ParameterDirection.Output);
        await c.ExecuteAsync("dash.usp_GetSecret", p, commandType: CommandType.StoredProcedure);
        return p.Get<string?>("@Value");
    }

    /// <summary>Stores the secret; null or empty removes it.</summary>
    public Task SetAsync(string name, string? value, string by) =>
        db.ExecuteAsync("EXEC dash.usp_SetSecret @Name = @name, @Value = @value, @By = @by;", new { name, value, by });
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
    public bool AcceptAnyCertificate { get; init; }
    /// <summary>Exchange Online: Password (SMTP AUTH) or OAuth (client credentials, SMTP.SendAsApp).</summary>
    public string ExoAuth { get; init; } = "Password";
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public const string ExchangeOnlineHost = "smtp.office365.com";
    public static readonly string[] SecurityChoices = ["StartTls", "Implicit", "Auto", "None"];

    public static MailConfig Load(IReadOnlyDictionary<string, string?> s, string? password, string? clientSecret)
    {
        string? V(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        return new MailConfig
        {
            Mode = Enum.TryParse<MailMode>(V("MailMode"), true, out var m) ? m : MailMode.Off,
            From = V("MailFrom"), ReplyTo = V("MailReplyTo"), Host = V("SmtpHost"),
            Port = int.TryParse(V("SmtpPort"), out var port) ? port : 587,
            Security = V("SmtpSecurity") is { } sec && SecurityChoices.Contains(sec, StringComparer.OrdinalIgnoreCase) ? SecurityChoices.First(x => x.Equals(sec, StringComparison.OrdinalIgnoreCase)) : "StartTls",
            User = V("SmtpUser"), Password = password, AcceptAnyCertificate = V("SmtpAcceptAnyCertificate") == "1",
            ExoAuth = V("ExoAuth") is "OAuth" ? "OAuth" : "Password",
            TenantId = V("ExoTenantId"), ClientId = V("ExoClientId"), ClientSecret = clientSecret,
        };
    }

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
        return MailConfig.Load(s, await secrets.GetAsync(SecretStore.SmtpPassword), await secrets.GetAsync(SecretStore.ExoClientSecret));
    }

    /// <summary>Sends one message and logs it. Returns null when sent, else why not.</summary>
    public async Task<string?> SendAsync(string kind, IReadOnlyList<MailboxAddress> to, string subject, string html, string text, CancellationToken ct = default)
    {
        string? error;
        string status;
        if (to.Count == 0) { status = "NoRecipients"; error = "No recipients are set."; }
        else
        {
            var cfg = await ConfigAsync();
            var problems = cfg.Problems();
            if (problems.Count > 0) { status = cfg.Mode == MailMode.Off ? "MailOff" : "Failed"; error = string.Join(" ", problems); }
            else
            {
                try { await SendCoreAsync(cfg, to, subject, html, text, ct); status = "Sent"; error = null; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    status = "Failed"; error = Describe(ex);
                    log.LogWarning(ex, "Email '{Subject}' was not sent", subject);
                }
            }
        }
        try
        {
            await db.ExecuteAsync("INSERT INTO dash.NotificationLog (Kind, Subject, Recipients, Status, Error) VALUES (@kind, @subject, @to, @status, @error);",
                new { kind, subject = Cut(subject, 400), to = Cut(string.Join("; ", to.Select(a => a.Address)), 2000), status, error = error is null ? null : Cut(error, 2000) });
        }
        catch (Exception ex) { log.LogError(ex, "Could not write dash.NotificationLog"); }
        return error;
    }

    async Task SendCoreAsync(MailConfig cfg, IReadOnlyList<MailboxAddress> to, string subject, string html, string text, CancellationToken ct)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(cfg.From!));
        if (cfg.ReplyTo is not null) msg.ReplyTo.Add(MailboxAddress.Parse(cfg.ReplyTo));
        foreach (var a in to) msg.To.Add(a);
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 60_000 };
        if (cfg.AcceptAnyCertificate) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var (host, port, tls) = cfg.Endpoint();
        await client.ConnectAsync(host, port, tls, ct);
        if (cfg.Mode == MailMode.ExchangeOnline && cfg.ExoAuth == "OAuth")
            await client.AuthenticateAsync(new SaslMechanismOAuth2(cfg.From!, await TokenAsync(cfg, ct)), ct);
        else if (cfg.Mode == MailMode.ExchangeOnline)
            await client.AuthenticateAsync(cfg.User ?? cfg.From!, cfg.Password!, ct);
        else if (cfg.User is not null)
            await client.AuthenticateAsync(cfg.User, cfg.Password!, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }

    sealed record TokenReply([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);

    /// <summary>An app-only token for Exchange Online SMTP (scope outlook.office365.com), cached until shortly before it expires.</summary>
    async Task<string> TokenAsync(MailConfig cfg, CancellationToken ct)
    {
        var key = $"exo-token:{cfg.TenantId}:{cfg.ClientId}";   // per app, so the same across dashboard tenants
        if (cache.TryGetValue(key, out string? t) && t is not null) return t;
        using var http = httpFactory.CreateClient();
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
