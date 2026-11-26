using System.Text.Json;
using Governance.Application;
using Governance.Application.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;

namespace Governance.Infrastructure;

public static class ServiceCollectionExtensions
{
    public const string GraphScope = "https://graph.microsoft.com/.default";
    public const string PowerPlatformScope = "https://api.powerplatform.com/.default";

    /// <summary>
    /// Registers the integration adapters (Graph, Power Platform, ITSM, mail, Teams, evidence scanning), the sync
    /// services and the application core. The web and the worker both call this; each registers its own ICurrentPrincipal.
    /// </summary>
    public static IServiceCollection AddGovernanceInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddGovernanceCore();

        services.AddSingleton(sp => LoadTenantSettings(configuration, SearchForLocalFiles(sp.GetService<IHostEnvironment>())));
        services.Configure<GraphOptions>(configuration.GetSection("Graph"));
        services.Configure<PowerPlatformOptions>(configuration.GetSection("PowerPlatform"));
        services.Configure<ItsmOptions>(configuration.GetSection("Itsm"));
        services.Configure<NotificationOptions>(configuration.GetSection("Notifications"));
        services.AddSingleton<IAppTokenProvider, CertificateTokenProvider>();
        services.AddTransient<CorrelationHandler>();

        // Graph: app-only token, correlation ID, standard resilience (retry with jitter, honors Retry-After on 429, timeout, circuit breaker).
        services.AddHttpClient<GraphClient>(c => c.Timeout = TimeSpan.FromMinutes(2))
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddHttpMessageHandler(sp => new AppTokenHandler(sp.GetRequiredService<IAppTokenProvider>(), GraphScope))
            .AddStandardResilienceHandler();
        services.AddTransient<IDirectoryApi>(sp => sp.GetRequiredService<GraphClient>());
        services.AddTransient<IEntitlementApi>(sp => sp.GetRequiredService<GraphClient>());
        services.AddTransient<IAccessReviewApi>(sp => sp.GetRequiredService<GraphClient>());
        services.AddTransient<ISeatLicenseApi>(sp => sp.GetRequiredService<GraphClient>());
        // Seat groups from on-premises AD when Graph's group permission is not granted (Seats.GroupMembersFrom).
        if (OperatingSystem.IsWindows()) services.AddSingleton<IAdGroupApi, AdGroupReader>();
        // Copilot activity from the Copilot Insights database, when its connection is given: the seat read prefers it.
        if (configuration.GetConnectionString("Insights") is { Length: > 0 } insights)
            services.AddSingleton<IInsightsActivitySource>(new InsightsActivitySource(insights));

        // Power Platform: app-only unless a delegated token is passed per call.
        services.AddHttpClient<PowerPlatformClient>(c => c.Timeout = TimeSpan.FromMinutes(2))
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddHttpMessageHandler(sp => new AppTokenHandler(sp.GetRequiredService<IAppTokenProvider>(), PowerPlatformScope))
            .AddStandardResilienceHandler();
        services.AddTransient<IPowerPlatformApi>(sp => sp.GetRequiredService<PowerPlatformClient>());

        // ITSM: no automatic retry of POST (a timed-out create could duplicate); the outbox retries after a search by correlation ID.
        services.AddHttpClient<IItsmClient, ServiceNowItsmClient>()
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());

        services.AddHttpClient<FlowChannelPoster>()
            .AddHttpMessageHandler<CorrelationHandler>()
            .AddStandardResilienceHandler();
        services.AddTransient<IChannelPoster>(sp => sp.GetRequiredService<FlowChannelPoster>());

        var record = !string.Equals(configuration["Notifications:Mode"], "Send", StringComparison.OrdinalIgnoreCase);
        if (record)
        {
            services.AddSingleton<RecordingSenders>();
            services.AddTransient<IMailSender>(sp => sp.GetRequiredService<RecordingSenders>());
            services.AddTransient<ITeamsActivitySender>(sp => sp.GetRequiredService<RecordingSenders>());
            services.AddTransient<ITeamsActivityBulkSender>(sp => sp.GetRequiredService<RecordingSenders>());
        }
        else
        {
            services.AddTransient<IMailSender, FallbackMailSender>();
            services.AddTransient<ITeamsActivitySender>(sp => sp.GetRequiredService<GraphClient>());
            services.AddTransient<ITeamsActivityBulkSender>(sp => sp.GetRequiredService<GraphClient>());
        }
        services.AddSingleton<IEvidenceScanner, SniffingEvidenceScanner>();

        // Sync services (Application) over these adapters.
        services.AddSingleton<FindingWriter>();
        services.AddSingleton<DriftEvaluator>();
        services.AddSingleton<RecipientResolver>();
        services.AddScoped<DirectorySyncService>();
        services.AddScoped<EntitlementSyncService>();
        services.AddScoped<AccessReviewImportService>();
        services.AddScoped<IInventorySync, InventorySyncService>();
        services.AddScoped<ILicensingRead, LicensingReadService>();
        services.AddScoped<IRegistryImport, RegistryImportService>();
        services.AddScoped<IIntegrationStatus, IntegrationStatusService>();
        services.AddScoped<SeatSyncService>();
        services.AddScoped<ItsmPollService>();
        services.AddScoped<RetentionService>();
        return services;
    }

    /// <summary>
    /// Whether the test tenant's file (config\tenant.test.json) is looked for upward from the program's folder: in
    /// Development, and where no host says otherwise (tests and tools). A production site or worker reads its settings
    /// files only, so a stray file above the install folder never changes which tenant it talks to.
    /// </summary>
    public static bool SearchForLocalFiles(IHostEnvironment? environment) => environment is null || environment.IsDevelopment();

    /// <summary>
    /// Tenant IDs from setup\Setup-TestTenant.ps1. Workbench:TenantSettingsPath wins; otherwise, when
    /// <paramref name="searchUpward"/>, config\tenant.test.json is looked for upward from the content root. Missing
    /// settings give an empty object: integrations then report themselves as not configured instead of the host failing
    /// to start. The "Tenant" section of the settings wins over either.
    /// </summary>
    public static TenantSettings LoadTenantSettings(IConfiguration configuration, bool searchUpward = true)
    {
        var path = configuration["Workbench:TenantSettingsPath"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = null;
            for (var dir = searchUpward ? new DirectoryInfo(AppContext.BaseDirectory) : null; dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "config", "tenant.test.json");
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }
        var settings = path is null
            ? new TenantSettings()
            : JsonSerializer.Deserialize<TenantSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new TenantSettings();
        // Production keeps the IDs in the settings file instead (section "Tenant"): any value there wins.
        configuration.GetSection("Tenant").Bind(settings);
        if (configuration["Notifications:Mailbox"] is { Length: > 0 } mailbox) settings.Mailbox = mailbox;
        return settings;
    }
}
