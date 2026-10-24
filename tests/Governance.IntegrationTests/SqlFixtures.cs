using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Governance.Seed;
using Microsoft.Data.SqlClient;

namespace Governance.IntegrationTests;

[CollectionDefinition("sql", DisableParallelization = true)]
public sealed class SqlCollection;

/// <summary>
/// A throwaway database deployed with the real Deploy-Database.ps1 (SQL logins for app_web and app_worker),
/// dropped with its logins at the end. Needs a local SQL Server with Windows authentication.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    public string Server { get; } = Environment.GetEnvironmentVariable("CGW_IT_SERVER") ?? "localhost";
    public string Database { get; } = "CopilotGovIt_" + Guid.NewGuid().ToString("n")[..10];
    public string AdminConnectionString => $"Server={Server};Database={Database};Integrated Security=True;TrustServerCertificate=True;Encrypt=True;Pooling=False";
    public string WebConnectionString { get; private set; } = "";
    public string WorkerConnectionString { get; private set; } = "";
    public string RepoRoot { get; } = FindRepoRoot();
    private string _settingsPath = "";

    public virtual async Task InitializeAsync()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"{Database}.json");
        Deploy(createLogins: true);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_settingsPath));
        WebConnectionString = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Governance").GetString()! + ";Pooling=False";
        WorkerConnectionString = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("GovernanceWorker").GetString()! + ";Pooling=False";
    }

    public void Deploy(bool createLogins)
    {
        var script = Path.Combine(RepoRoot, "src", "Governance.Database", "Deploy-Database.ps1");
        var args = new List<string> { "-NoProfile", "-File", script, "-SqlServer", Server, "-Database", Database };
        if (createLogins) args.AddRange(new[] { "-CreateSqlLogins", "-LocalSettingsPath", _settingsPath });
        var psi = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { p.Kill(true); throw new TimeoutException("Deploy-Database.ps1 did not finish in 5 minutes."); }
        if (p.ExitCode != 0) throw new InvalidOperationException($"Deploy failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
    }

    public SqlConnection Admin() { var c = new SqlConnection(AdminConnectionString); c.Open(); return c; }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        var master = $"Server={Server};Database=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=True;Pooling=False";
        await using var c = new SqlConnection(master);
        await c.OpenAsync();
        var suffix = Database.ToLowerInvariant();
        await c.ExecuteAsync($"""
            IF DB_ID(N'{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END
            IF SUSER_ID(N'cgw_web_{suffix}') IS NOT NULL DROP LOGIN [cgw_web_{suffix}];
            IF SUSER_ID(N'cgw_worker_{suffix}') IS NOT NULL DROP LOGIN [cgw_worker_{suffix}];
            """);
        if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CopilotGovernance.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("CopilotGovernance.sln not found above the test output folder.");
    }
}

/// <summary>A deployed database with the demo foundation seeded (no sample requests): personas, funding owners, envelopes, intake open.</summary>
public sealed class SeededFixture : DatabaseFixture
{
    public SeedResult Seed { get; private set; } = null!;
    public EngineHost Host { get; private set; } = null!;
    public RequestFlows Flows { get; private set; } = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Host = new EngineHost(AdminConnectionString);
        Seed = await new DemoSeeder(AdminConnectionString, Host.Clock.UtcNow).SeedAsync(tenant: null);
        Flows = new RequestFlows(Host, Seed);
    }
}
