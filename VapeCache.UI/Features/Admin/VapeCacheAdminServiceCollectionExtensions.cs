using Microsoft.Extensions.DependencyInjection;

namespace VapeCache.UI.Features.Admin;

/// <summary>
/// DI registrations for VapeCache admin UI orchestration adapters.
/// </summary>
internal static class VapeCacheAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers admin adapter contracts and orchestrator.
    /// </summary>
    public static IServiceCollection AddVapeCacheAdminUi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVapeCacheAdminStatsSnapshotProvider, RuntimeVapeCacheAdminStatsSnapshotProvider>();
        services.AddScoped<IVapeCacheAdminInvalidationOperationsFacade, RuntimeVapeCacheAdminInvalidationOperationsFacade>();
        services.AddScoped<IVapeCacheAdminAutoscalerStatusProvider, RuntimeVapeCacheAdminAutoscalerStatusProvider>();
        services.AddScoped<IVapeCacheAdminSpillDiagnosticsProvider, RuntimeVapeCacheAdminSpillDiagnosticsProvider>();
        services.AddScoped<IVapeCacheAdminReconciliationStatusProvider, RuntimeVapeCacheAdminReconciliationStatusProvider>();
        services.AddScoped<IVapeCacheAdminBreakerStatusProvider, RuntimeVapeCacheAdminBreakerStatusProvider>();
        services.AddScoped<IVapeCacheAdminPolicyInspectionProvider, RuntimeVapeCacheAdminPolicyInspectionProvider>();
        services.AddScoped<IVapeCacheAdminEventStreamFeedProvider, RuntimeVapeCacheAdminEventStreamFeedProvider>();
        services.AddScoped<VapeCacheAdminOrchestrator>();

        return services;
    }
}

