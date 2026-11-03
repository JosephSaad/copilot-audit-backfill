// Sends ONE test email from the notification mailbox and ONE Teams activity-feed notice to the person you name,
// through the same adapters the worker uses (Graph sendMail scoped by Exchange RBAC for Applications; Teams
// activity feed with resource-specific consent). Nothing else is sent; the outbox is not touched.
//
//   dotnet run --project tools/Governance.SendTestNotice -- --to someone@contoso.com [--no-teams]
using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Governance.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

string? to = null;
var teams = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--to" && i + 1 < args.Length) to = args[++i];
    else if (args[i] == "--no-teams") teams = false;
}
if (string.IsNullOrWhiteSpace(to) || !to.Contains('@'))
{
    Console.Error.WriteLine("Usage: --to <user principal name> [--no-teams]");
    return 2;
}

var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
var local = FindUpward("config", "appsettings.Local.json");
if (local is not null) builder.Configuration.AddJsonFile(local, optional: true);
builder.Configuration["Notifications:Mode"] = "Send";   // this tool always sends; the worker keeps its own mode
builder.Services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
builder.Services.Configure<DbOptions>(o => o.ConnectionString = builder.Configuration.GetConnectionString("GovernanceWorker") ?? "");
builder.Services.AddLogging();
builder.Services.AddGovernanceInfrastructure(builder.Configuration);
using var host = builder.Build();

var tenant = host.Services.GetRequiredService<TenantSettings>();
var baseUrl = tenant.WebBaseUrl ?? "http://localhost:5450";
var ct = CancellationToken.None;

var mail = host.Services.GetRequiredService<IMailSender>();
await mail.SendAsync(new MailMessageData(
    new[] { to },
    "Copilot Governance Workbench: test notice",
    $"<p>This is a test notice from the Copilot Governance Workbench test environment.</p><p>Nothing needs your action. The Workbench never asks for an approval by email: decisions happen in the Workbench.</p><p><a href=\"{baseUrl}/Start\">Open the Workbench</a></p>",
    $"This is a test notice from the Copilot Governance Workbench test environment.\nNothing needs your action. Decisions happen in the Workbench, never by email.\nOpen the Workbench: {baseUrl}/Start",
    Guid.NewGuid().ToString()), ct);
Console.WriteLine($"Email sent to {to} from {tenant.Mailbox}.");

if (teams)
{
    // The activity feed needs the person's Entra object ID, which the directory sync stored on sec.Person.
    await using var conn = new Microsoft.Data.SqlClient.SqlConnection(builder.Configuration.GetConnectionString("GovernanceWorker"));
    await conn.OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT TOP (1) EntraObjectId FROM sec.Person WHERE UserPrincipalName = @upn AND EntraObjectId IS NOT NULL";
    cmd.Parameters.AddWithValue("@upn", to);
    var oid = await cmd.ExecuteScalarAsync(ct) as Guid?;
    if (oid is null) { Console.Error.WriteLine($"{to} is not in sec.Person yet (run the directory sync first)."); return 1; }
    var teamsSender = host.Services.GetRequiredService<ITeamsActivitySender>();
    await teamsSender.SendAsync(oid.Value, "taskAssigned", "Test notice from the Governance Workbench", "/workbench/Start",
        new Dictionary<string, string> { ["taskTitle"] = "a test task (no action needed)" }, ct);   // {actor} is filled by Teams
    Console.WriteLine($"Teams activity notice sent to {to}.");
}
return 0;

static string? FindUpward(string folder, string file)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, folder, file);
        if (File.Exists(candidate)) return candidate;
    }
    return null;
}
