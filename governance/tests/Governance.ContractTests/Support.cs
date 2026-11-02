using System.Net;
using System.Text;
using Governance.Application.Integrations;
using Governance.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Governance.ContractTests;

/// <summary>Replays recorded responses: the first route whose predicate matches the request answers it. Requests are kept for assertions.</summary>
public sealed class ReplayHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
    public List<(HttpMethod Method, string Url, string? Body, string? Authorization)> Requests { get; } = new();

    public ReplayHandler On(Func<HttpRequestMessage, bool> match, string recordedFile, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((match, _ => new HttpResponseMessage(status) { Content = new StringContent(Recorded.Read(recordedFile), Encoding.UTF8, "application/json") }));
        return this;
    }

    public ReplayHandler OnStatus(Func<HttpRequestMessage, bool> match, HttpStatusCode status, string body = "{}")
    {
        _routes.Add((match, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        foreach (var (match, respond) in _routes)
            if (match(request)) return respond(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"NoRecording\"}}") };
    }
}

public static class Recorded
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recorded", name));
}

public sealed class FakeTokens : IAppTokenProvider
{
    public List<string> Scopes { get; } = new();
    public Task<string> GetTokenAsync(string scope, CancellationToken ct) { Scopes.Add(scope); return Task.FromResult("app-token"); }
}

public static class Clients
{
    public static TenantSettings Tenant => new()
    {
        TenantId = "00000000-0000-0000-0000-000000000001", WorkerClientId = "19a68a93-1f6f-4034-9791-97b71816bfdc",
        Mailbox = "workbench@contoso.example", WorkerCertificateSubject = "CN=Test",
    };

    public static GraphClient Graph(ReplayHandler h) => new(new HttpClient(h), Options.Create(new GraphOptions()), Tenant, NullLogger<GraphClient>.Instance);
    public static PowerPlatformClient PowerPlatform(ReplayHandler h) => new(new HttpClient(h), Options.Create(new PowerPlatformOptions()));
    public static ServiceNowItsmClient Itsm(ReplayHandler h) => new(new HttpClient(h), Options.Create(new ItsmOptions { BaseUrl = "http://itsm.test", Username = "u", Password = "p" }));
}
