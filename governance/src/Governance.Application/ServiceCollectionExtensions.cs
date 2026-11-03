using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Governance.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>Core services shared by the web and the worker. The host registers <see cref="ICurrentPrincipal"/>.</summary>
    public static IServiceCollection AddGovernanceCore(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<BusinessCalendarProvider>();
        services.AddScoped<Db>();
        services.AddSingleton<OutboxWriter>();
        services.AddSingleton<AuditWriter>();
        services.AddSingleton<AssignmentResolver>();
        services.AddSingleton<JourneyEffects>();
        services.AddSingleton<WorkflowEngine>();
        return services;
    }
}
