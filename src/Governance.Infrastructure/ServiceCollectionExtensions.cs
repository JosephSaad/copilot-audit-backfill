using Governance.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Governance.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the integration adapters (Graph, Power Platform, ITSM, mail, Teams, evidence scanning) and the
    /// application core. The web and the worker both call this; each registers its own ICurrentPrincipal.
    /// </summary>
    public static IServiceCollection AddGovernanceInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddGovernanceCore();
        // Adapters are registered here by the integration module.
        return services;
    }
}
