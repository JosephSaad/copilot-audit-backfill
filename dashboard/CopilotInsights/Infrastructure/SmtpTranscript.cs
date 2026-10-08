using System.Text;
using MailKit.Security;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// What was said between the dashboard and the mail server while a message was sent, kept for troubleshooting
/// (dash.NotificationLog.Detail, shown on Admin > Email): where it connected and how, each command and the server's
/// answer to it, and how it ended. The password is never in it, nor the message itself.
/// </summary>
public static class SmtpTranscript
{
    /// <summary>The most kept of one conversation.</summary>
    public const int MaxLength = 16_000;

    /// <summary>The line that opens a transcript: where the message was sent, how, and as whom. Never the password.</summary>
    public static string Header(MailConfig cfg)
    {
        var (host, port, tls) = cfg.Endpoint();
        var how = tls switch
        {
            SecureSocketOptions.None => "no encryption",
            SecureSocketOptions.SslOnConnect => "TLS from the first byte (Implicit)",
            SecureSocketOptions.StartTls => "STARTTLS, required",
            SecureSocketOptions.StartTlsWhenAvailable => "STARTTLS if the server offers it",
            _ => "TLS chosen by port (Auto)",
        };
        var who = cfg.Mode == MailMode.ExchangeOnline && cfg.ExoAuth == "OAuth" ? $"signing in with OAuth as {cfg.From}"
            : (cfg.Mode == MailMode.ExchangeOnline ? cfg.User ?? cfg.From : cfg.User) is { Length: > 0 } user
                ? $"signing in as {user} ({(string.IsNullOrEmpty(cfg.Password) ? "no password stored" : "password stored")})"
                : "not signing in (no user name is set)";
        return $"Server {host}, port {port}, {how}; {who}; from {cfg.From}." +
               (cfg.AcceptAnyCertificate ? " The server's certificate is not checked (Accept any server certificate)." : "");
    }

    /// <summary>
    /// The protocol log MailKit wrote, made safe to keep: the message's own lines are left out (after the server's 354,
    /// up to its next answer), what follows an AUTH command is masked whatever the library already hid, and it is cut
    /// to <see cref="MaxLength"/>. Lines are "C: " (the dashboard) and "S: " (the server).
    /// </summary>
    public static string Clean(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return "";
        var sb = new StringBuilder();
        bool data = false, auth = false;
        foreach (var raw in log.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var server = line.StartsWith("S: ", StringComparison.Ordinal);
            if (server)
            {
                if (data) { sb.Append("C: (the message itself is not kept)\n"); data = false; }
                /* 334 asks for the next part of a sign-in; any other answer ends it. */
                if (auth && !line.StartsWith("S: 334", StringComparison.Ordinal)) auth = false;
                sb.Append(line).Append('\n');
                if (line.StartsWith("S: 354", StringComparison.Ordinal)) data = true;
                continue;
            }
            if (data) continue;
            if (auth) { sb.Append("C: ********\n"); continue; }
            if (line.StartsWith("C: AUTH ", StringComparison.OrdinalIgnoreCase))
            {
                /* AUTH LOGIN, AUTH PLAIN <credentials>, AUTH XOAUTH2 <token>: the mechanism is kept, what follows it is not. */
                var parts = line.Split(' ', 4);
                sb.Append(parts.Length > 3 ? $"{parts[0]} {parts[1]} {parts[2]} ********" : line).Append('\n');
                auth = true;
                continue;
            }
            sb.Append(line).Append('\n');
        }
        if (data) sb.Append("C: (the message itself is not kept)\n");
        var text = sb.ToString().TrimEnd();
        return text.Length <= MaxLength ? text : text[..MaxLength] + "\n(cut: the conversation was longer than is kept)";
    }

    /// <summary>How it ended, with every cause down the chain: the last line of a transcript that failed.</summary>
    public static string Failure(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 6; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return "Not sent. " + string.Join(" <- ", parts);
    }

    /// <summary>The whole record of one send: the header, the conversation, and the ending.</summary>
    public static string Compose(MailConfig cfg, string? log, Exception? failure) =>
        string.Join("\n", new[] { Header(cfg), Clean(log), failure is null ? "Sent: the server accepted the message." : Failure(failure) }.Where(s => s.Length > 0));
}
