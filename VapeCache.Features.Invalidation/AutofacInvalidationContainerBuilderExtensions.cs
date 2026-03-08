using Autofac;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VapeCache.Features.Invalidation;

/// <summary>
/// Autofac registrations for invalidation runtime and policies.
/// </summary>
public static class AutofacInvalidationContainerBuilderExtensions
{
    /// <summary>
    /// Executes add vape cache invalidation.
    /// </summary>
    public static ContainerBuilder AddVapeCacheInvalidation(
        this ContainerBuilder builder,
        Action<CacheInvalidationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new CacheInvalidationOptions();
        configure?.Invoke(options);

        // Keep this package self-contained while preserving host-provided logging.
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance)
            .As<ILoggerFactory>()
            .SingleInstance()
            .IfNotRegistered(typeof(ILoggerFactory));
        builder.RegisterGeneric(typeof(Logger<>))
            .As(typeof(ILogger<>))
            .SingleInstance()
            .IfNotRegistered(typeof(ILogger<>));

        builder.RegisterInstance(new StaticOptionsMonitor<CacheInvalidationOptions>(options))
            .As<IOptions<CacheInvalidationOptions>>()
            .As<IOptionsMonitor<CacheInvalidationOptions>>()
            .SingleInstance();

        builder.Register(ctx => (IServiceProvider)new AutofacBackedServiceProvider(ctx.Resolve<ILifetimeScope>()))
            .As<IServiceProvider>()
            .SingleInstance();

        builder.RegisterType<CacheInvalidationExecutor>()
            .As<ICacheInvalidationExecutor>()
            .SingleInstance();

        builder.RegisterType<CacheInvalidationDispatcher>()
            .As<ICacheInvalidationDispatcher>()
            .SingleInstance();

        return builder;
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddCacheInvalidationPolicy<TEvent, TPolicy>(this ContainerBuilder builder)
        where TPolicy : class, ICacheInvalidationPolicy<TEvent>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.RegisterType<TPolicy>()
            .As<ICacheInvalidationPolicy<TEvent>>()
            .SingleInstance();

        return builder;
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddCacheInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        Func<IComponentContext, ICacheInvalidationPolicy<TEvent>> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Register(ctx => factory(ctx))
            .As<ICacheInvalidationPolicy<TEvent>>()
            .SingleInstance();

        return builder;
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddTagInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        Func<TEvent, IEnumerable<string>?> tagsSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tagsSelector);

        return builder.AddCacheInvalidationPolicy<TEvent>(_ =>
            new TagInvalidationPolicy<TEvent>(tagsSelector, predicate));
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddZoneInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        Func<TEvent, IEnumerable<string>?> zonesSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(zonesSelector);

        return builder.AddCacheInvalidationPolicy<TEvent>(_ =>
            new ZoneInvalidationPolicy<TEvent>(zonesSelector, predicate));
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddKeyInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        Func<TEvent, IEnumerable<string>?> keysSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keysSelector);

        return builder.AddCacheInvalidationPolicy<TEvent>(_ =>
            new KeyInvalidationPolicy<TEvent>(keysSelector, predicate));
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddEntityInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        IEnumerable<string>? keyPrefixes = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return builder.AddCacheInvalidationPolicy<TEvent>(_ =>
            new EntityInvalidationPolicy<TEvent>(
                entityName,
                idsSelector,
                zonesSelector,
                keyPrefixes,
                predicate));
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddSmallWebsiteEntityInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return builder.AddEntityInvalidationPolicy(
            entityName: entityName,
            idsSelector: idsSelector,
            zonesSelector: zonesSelector,
            keyPrefixes: [],
            predicate: predicate);
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddHighTrafficEntityInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        string entityName,
        Func<TEvent, IEnumerable<string>?> idsSelector,
        Func<TEvent, IEnumerable<string>?>? zonesSelector = null,
        IEnumerable<string>? keyPrefixes = null,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(idsSelector);

        return builder.AddEntityInvalidationPolicy(
            entityName: entityName,
            idsSelector: idsSelector,
            zonesSelector: zonesSelector,
            keyPrefixes: keyPrefixes ?? [entityName],
            predicate: predicate);
    }

    /// <summary>
    /// Provides member behavior.
    /// </summary>
    public static ContainerBuilder AddDesktopKeyInvalidationPolicy<TEvent>(
        this ContainerBuilder builder,
        Func<TEvent, IEnumerable<string>?> keysSelector,
        Func<TEvent, bool>? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keysSelector);

        return builder.AddKeyInvalidationPolicy(keysSelector, predicate);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>, IOptions<T>
        where T : class
    {
        private readonly T _value = value;

        public T CurrentValue => _value;

        public T Value => _value;

        public T Get(string? name) => _value;

        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class AutofacBackedServiceProvider(ILifetimeScope scope) : IServiceProvider
    {
        private readonly ILifetimeScope _scope = scope;

        public object? GetService(Type serviceType)
            => _scope.ResolveOptional(serviceType);
    }
}
