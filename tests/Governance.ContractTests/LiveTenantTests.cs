using Governance.Application.Integrations;
using Governance.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Governance.ContractTests;

/// <summary>Runs only when CGW_LIVE=1: read-only calls against the test tenant with the Worker certificate.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CGW_LIVE") != "1") Skip = "Live tenant test: set CGW_LIVE=1 (read-only calls with the Worker certificate).";
    }
}

/// <summary>
/// Re-records the shapes the adapters depend on against the test tenant (read only). Counts are asserted, never
/// names: responses are not written to the repository because they hold personal data.
/// </summary>
[Trait("Category", "Live")]
public class LiveTenantTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Notifications:Mode"] = "Record" })
            .AddEnvironmentVariables("CGW_")
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<Governance.Application.ICurrentPrincipal>(Governance.Application.SystemPrincipal.Instance);
        services.AddGovernanceInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [LiveFact]
    public async Task Directory_delta_first_page_reads_users()
    {
        using var sp = Build();
        var page = await sp.GetRequiredService<IDirectoryApi>().UsersDeltaAsync(null, default);
        Assert.NotEmpty(page.Items);
        Assert.True(page.NextLink is not null || page.DeltaLink is not null);
    }

    [LiveFact]
    public async Task Governed_access_packages_are_readable_with_the_catalog_reader_role()
    {
        using var sp = Build();
        var tenant = sp.GetRequiredService<TenantSettings>();
        var api = sp.GetRequiredService<IEntitlementApi>();
        var packages = tenant.PolicyGroups.Select(p => Guid.TryParse(p.AccessPackageId, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        Assert.NotEmpty(packages);
        foreach (var p in packages) _ = await api.AssignmentsAsync(p, default);   // no permission error
    }
}
