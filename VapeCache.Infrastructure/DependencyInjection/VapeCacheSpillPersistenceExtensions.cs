using Autofac;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Infrastructure.DependencyInjection;

/// <summary>
/// Autofac helpers for enabling segmented spill persistence.
/// </summary>
public static class VapeCacheSpillPersistenceExtensions
{
    /// <summary>
    /// Registers segmented spill persistence as the active spill store.
    /// </summary>
    public static ContainerBuilder RegisterVapeCachePersistence(this ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterType<SegmentedLogSpillStore>()
            .AsSelf()
            .As<IInMemorySpillStore>()
            .As<ISpillStoreDiagnostics>()
            .SingleInstance()
            .OnActivated(e => CacheTelemetry.InitializeSpillDiagnostics(e.Instance));
        return builder;
    }
}
