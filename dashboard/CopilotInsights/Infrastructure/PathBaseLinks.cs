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

    public static IApplicationBuilder UsePathBaseLinks(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var pathBase = ctx.Request.PathBase.Value;
        if (string.IsNullOrEmpty(pathBase)) { await next(); return; }
        ctx.Response.OnStarting(() =>
        {
            if (ctx.Response.Headers.Location.ToString() is { Length: > 0 } loc) ctx.Response.Headers.Location = Location(loc, pathBase);
            return Task.CompletedTask;
        });
        /* Only a page is read through: not a file, a download, a form's answer (a redirect) or the data API. */
        var path = ctx.Request.Path.Value ?? "";
        if (!HttpMethods.IsGet(ctx.Request.Method) || ctx.Request.Query.ContainsKey("handler") || ctx.Request.Path.StartsWithSegments("/api") || Path.HasExtension(path))
        {
            await next();
            return;
        }
        var body = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await next(); }
        finally { ctx.Response.Body = body; }
        buffer.Position = 0;
        if (ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            var html = Rewrite(await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(), pathBase);
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength = bytes.Length;
            await body.WriteAsync(bytes);
        }
        else await buffer.CopyToAsync(body);
    });
}
