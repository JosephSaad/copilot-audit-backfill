using Governance.Seed;

// Demo seed for the Copilot Governance Workbench test environment.
//   dotnet run --project tools/Governance.Seed -- --database CopilotGovernanceTest [--server localhost]
//       [--tenant-settings <path to config\tenant.test.json>] [--sample-requests] [--force]
// Connects with Windows authentication as the deployment principal. Safe to rerun: a seeded database is left alone.

var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    var key = args[i][2..];
    var value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
    opts[key] = value;
}
if (!opts.TryGetValue("database", out var database) || string.IsNullOrWhiteSpace(database))
{
    Console.Error.WriteLine("Usage: Governance.Seed --database <name> [--server localhost] [--tenant-settings <path>] [--sample-requests] [--force]");
    return 2;
}
var server = opts.GetValueOrDefault("server") ?? "localhost";
// The test tenant's settings, written by governance\setup\Setup-TestTenant.ps1: governance\config\tenant.test.json in this checkout.
var defaultSettings = Path.Combine(FindGovernanceRoot(AppContext.BaseDirectory), "config", "tenant.test.json");
var settingsPath = opts.GetValueOrDefault("tenant-settings") ?? defaultSettings;
var cs = $"Server={server};Database={database};Integrated Security=True;TrustServerCertificate=True;Encrypt=True;Application Name=CopilotGovernance-Seed";

var tenant = TenantSettings.Load(settingsPath);
Console.WriteLine(tenant is null ? $"No tenant settings at {settingsPath}: access packages and central groups are not mapped." : $"Tenant settings: {settingsPath}");

var host = new EngineHost(cs);
var seed = await new DemoSeeder(cs, host.Clock.UtcNow).SeedAsync(tenant, opts.ContainsKey("force"), Console.Out);
if (opts.ContainsKey("sample-requests"))
    await new SampleRequests(host, seed, Console.Out).RunAsync();
Console.WriteLine("Done.");
return 0;

static string FindGovernanceRoot(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "CopilotGovernance.sln"))) return d.FullName;
    return start;
}
