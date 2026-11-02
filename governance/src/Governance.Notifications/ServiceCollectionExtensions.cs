using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Governance.Notifications;

public static class ServiceCollectionExtensions
{
    /// <summary>Email rendering, the outbox processor and the notice delivery handler (the worker adds its own handlers, such as ITSM).</summary>
    public static IServiceCollection AddGovernanceNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DispatchOptions>(configuration.GetSection("Notifications"));
        services.AddSingleton<TextBlocks>();
        services.AddSingleton<EmailRenderer>();
        services.AddScoped<DeliveryLedger>();
        services.AddScoped<IOutboxHandler, NotificationDelivery>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<DigestBuilder>();
        return services;
    }
}
