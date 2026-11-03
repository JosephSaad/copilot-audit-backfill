using System.Text;
using System.Text.RegularExpressions;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The site under a virtual directory (https://apps.contoso.com/CopilotDashboard): IIS gives the application's path as
/// the request's PathBase, and every address the pages write from the site's root ("/licensing/...") is given it here,
/// on the way out. Links, sources, form actions and the person box's lookup address in each HTML page, and the address
/// a redirect sends to. An address that already carries the base (<see cref="ReportContext.Url"/> writes it), a full
/// address and one that starts "//" are left as they are. With no PathBase, the site at the root of its host, nothing
/// is read or changed.
/// </summary>
public static class PathBaseLinks
{
    static readonly Regex Attribute = new(@"(\s(?:href|src|action|formaction|data-lookup)="")/(?!/)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The page's root-relative addresses with the base before them. Pure, for the tests.</summary>
    public static string Rewrite(string html, string pathBase)
    {
        if (string.IsNullOrEmpty(pathBase) || pathBase == "/") return html;
        var b = pathBase.TrimEnd('/');
        return Attribute.Replace(html, m =>
        {
            /* Already under the base: the rest of the address starts with it. */
            var rest = html.AsSpan(m.Index + m.Length);
            var bare = b.AsSpan(1);
            if (rest.StartsWith(bare, StringComparison.OrdinalIgnoreCase) && (rest.Length == bare.Length || rest[bare.Length] is '/' or '"' or '?' or '#'))
                return m.Value;
            return m.Groups[1].Value + b + "/";
        });
    }

    /// <summary>A redirect's address with the base before it, where it is root-relative and does not carry it. Pure, for the tests.</summary>
    public static string Location(string location, string pathBase)
    {
        if (string.IsNullOrEmpty(pathBase) || pathBase == "/" || !location.StartsWith('/') || location.StartsWith("//", StringComparison.Ordinal)) return location;
        var b = pathBase.TrimEnd('/');
        return location.Equals(b, StringComparison.OrdinalIgnoreCase) || location.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith(b + "?", StringComparison.OrdinalIgnoreCase) ? location : b + location;
    }

    /// <summary>
    /// A virtual directory named as one of the dashboard's own top-level addresses: a link to that address could not be
    /// told from one already under the base. The installer refuses these names.
    /// </summary>
    public static bool Clashes(string pathBase) =>
        pathBase.Trim('/').Split('/')[0] is { Length: > 0 } first
        && CopilotInsights.Pages.Nav.Groups.SelectMany(g => g.Pages).Select(p => p.Path.Trim('/').Split('/')[0])
            .Concat(["api", "healthz", "not-found", "error", "me", "admin", "workbench"])
            .Contains(first, StringComparer.OrdinalIgnoreCase);

    public static IApplicationBuilder UsePathBaseLinks(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var pathBase = ctx.Request.PathBase.Value;
        if (string.IsNullOrEmpty(pathBase)) { await next(); return; }
        ctx.Response.OnStarting(() =>
        {
            if (ctx.Response.Headers.Location.ToString() is { Length: > 0 } loc) ctx.Response.Headers.Location = Location(loc, pathBase);
            return Task.CompletedTask;
        });
        /* Every response but the data API and files: an HTML page is read through and rewritten, whatever the method
           (a page drawn again after a form was refused), and anything else (a download) goes straight out. */
        var path = ctx.Request.Path.Value ?? "";
        if (ctx.Request.Path.StartsWithSegments("/api") || Path.HasExtension(path)) { await next(); return; }
        var body = ctx.Response.Body;
        await using var html = new HtmlOnly(ctx.Response, body);
        ctx.Response.Body = html;
        try { await next(); }
        finally { ctx.Response.Body = body; }
        if (html.Buffer is { } buffer)
        {
            buffer.Position = 0;
            var text = Rewrite(await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(), pathBase);
            var bytes = Encoding.UTF8.GetBytes(text);
            if (!ctx.Response.HasStarted) ctx.Response.ContentLength = bytes.Length;
            await body.WriteAsync(bytes);
        }
    });

    /// <summary>
    /// The response's body: kept in memory when the response is an HTML page, decided at its first write from its content
    /// type; anything else written straight through, so a large download is never held.
    /// </summary>
    sealed class HtmlOnly(HttpResponse response, Stream inner) : Stream
    {
        bool? _html;
        public MemoryStream? Buffer { get; private set; }

        Stream Target()
        {
            _html ??= response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
            if (_html == true) return Buffer ??= new MemoryStream();
            return inner;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { if (_html != true) inner.Flush(); }
        public override Task FlushAsync(CancellationToken token) => _html == true ? Task.CompletedTask : inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Target().Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Target().WriteAsync(buffer, offset, count, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => Target().WriteAsync(buffer, token);
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
