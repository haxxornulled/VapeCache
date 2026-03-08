using Autofac;
using Autofac.Configuration;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Hosting;
using VapeCache.Console.Plugins;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.DependencyInjection;
using VapeCache.Console.Stress;
using VapeCache.Console.Secrets;
using VapeCache.Console.Pos;
using VapeCache.Reconciliation;

var hostBuilder = Host.CreateDefaultBuilder(args)
    .UseServiceProviderFactory(new AutofacServiceProviderFactory())
    .ConfigureAppConfiguration(static (context, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        config.AddJsonFile("autofac.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();

        // Simulated "KeyVault" injection for Redis connection string.
        // Choose which env var to read from config (`RedisSecret:EnvVar`), then load the secret from that env var.
        // This keeps the actual secret out of appsettings and works consistently for VS/CLI.
        var temp = config.Build();
        var envVarName = temp["RedisSecret:EnvVar"];
        if (string.IsNullOrWhiteSpace(envVarName))
            envVarName = "VAPECACHE_REDIS_CONNECTIONSTRING";

        var required = false;
        if (bool.TryParse(temp["RedisSecret:Required"], out var parsedRequired))
            required = parsedRequired;

        var secret = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnection:ConnectionString"] = secret
            });
        }
        else
        {
            // Aspire references provide connection strings via ConnectionStrings:{name}.
            // Use this as a fallback when no explicit secret env var is set.
            var aspireRedisConnectionString = temp.GetConnectionString("redis");
            if (!string.IsNullOrWhiteSpace(aspireRedisConnectionString))
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RedisConnection:ConnectionString"] = aspireRedisConnectionString
                });
            }
        }
        // Never throw here; Redis can be unavailable or not configured. Startup preflight/failover controls behavior.
    })
    .UseSerilog(static (context, services, loggerConfig) =>
    {
        loggerConfig.ConfigureVapeCacheLogging(context.Configuration, services);
    })
    .ConfigureServices(static (context, services) =>
    {
        var otlpEndpoint = ResolveOtlpEndpoint(context.Configuration);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: "VapeCache.Console"))
            .WithMetrics(m =>
            {
                m.AddMeter("VapeCache.Redis");
                m.AddMeter("VapeCache.Cache");
                m.AddRuntimeInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddAspNetCoreInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    m.AddOtlpExporter(otlp =>
                    {
                        ConfigureOtlpForSignal(otlpEndpoint, otlp, signal: "metrics");
                    });
                }
            })
            .WithTracing(t =>
            {
                t.AddSource("VapeCache.Redis");
                t.AddHttpClientInstrumentation();
                t.AddAspNetCoreInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    t.AddOtlpExporter(otlp =>
                    {
                        ConfigureOtlpForSignal(otlpEndpoint, otlp, signal: "traces");
                    });
                }
            });

        services
            .AddOptions<RedisSecretOptions>()
            .Bind(context.Configuration.GetSection("RedisSecret"))
            .Validate(static o => !string.IsNullOrWhiteSpace(o.EnvVar), "RedisSecret:EnvVar is required.")
            .ValidateOnStart();

        services.AddOptions<LiveDemoOptions>()
            .Bind(context.Configuration.GetSection("LiveDemo"))
            .Validate(static o => o.Interval > TimeSpan.Zero, "LiveDemo:Interval must be > 0.")
            .Validate(static o => !string.IsNullOrWhiteSpace(o.Key), "LiveDemo:Key is required.")
            .Validate(static o => o.Ttl > TimeSpan.Zero, "LiveDemo:Ttl must be > 0.")
            .ValidateOnStart();
        services.AddOptions<PluginDemoOptions>()
            .Bind(context.Configuration.GetSection("PluginDemo"))
            .Validate(static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.KeyPrefix), "PluginDemo:KeyPrefix is required when enabled.")
            .Validate(static o => !o.Enabled || o.Ttl > TimeSpan.Zero, "PluginDemo:Ttl must be > 0 when enabled.")
            .ValidateOnStart();
        services.AddOptions<VapeCache.Console.GroceryStore.GroceryStoreStressOptions>()
            .Bind(context.Configuration.GetSection("GroceryStoreStress"))
            .Validate(static o => o.ConcurrentShoppers > 0, "GroceryStoreStress:ConcurrentShoppers must be > 0.")
            .Validate(static o => o.TotalShoppers > 0, "GroceryStoreStress:TotalShoppers must be > 0.")
            .Validate(static o => o.TargetDurationSeconds > 0, "GroceryStoreStress:TargetDurationSeconds must be > 0.")
            .Validate(static o => o.StartupDelaySeconds >= 0, "GroceryStoreStress:StartupDelaySeconds must be >= 0.")
            .Validate(static o => o.CountdownSeconds >= 0, "GroceryStoreStress:CountdownSeconds must be >= 0.")
            .Validate(static o => o.BrowseChancePercent is >= 0 and <= 100, "GroceryStoreStress:BrowseChancePercent must be 0..100.")
            .Validate(static o => o.BrowseMinProducts >= 0, "GroceryStoreStress:BrowseMinProducts must be >= 0.")
            .Validate(static o => o.BrowseMaxProducts >= o.BrowseMinProducts, "GroceryStoreStress:BrowseMaxProducts must be >= BrowseMinProducts.")
            .Validate(static o => o.FlashSaleJoinChancePercent is >= 0 and <= 100, "GroceryStoreStress:FlashSaleJoinChancePercent must be 0..100.")
            .Validate(static o => o.AddToCartChancePercent is >= 0 and <= 100, "GroceryStoreStress:AddToCartChancePercent must be 0..100.")
            .Validate(static o => o.CartItemsMin >= 0, "GroceryStoreStress:CartItemsMin must be >= 0.")
            .Validate(static o => o.CartItemsMax >= o.CartItemsMin, "GroceryStoreStress:CartItemsMax must be >= CartItemsMin.")
            .Validate(static o => o.CartItemQuantityMin >= 1, "GroceryStoreStress:CartItemQuantityMin must be >= 1.")
            .Validate(static o => o.CartItemQuantityMax >= o.CartItemQuantityMin, "GroceryStoreStress:CartItemQuantityMax must be >= CartItemQuantityMin.")
            .Validate(static o => o.ViewCartChancePercent is >= 0 and <= 100, "GroceryStoreStress:ViewCartChancePercent must be 0..100.")
            .Validate(static o => o.CheckoutChancePercent is >= 0 and <= 100, "GroceryStoreStress:CheckoutChancePercent must be 0..100.")
            .Validate(static o => o.RemoveFromCartChancePercent is >= 0 and <= 100, "GroceryStoreStress:RemoveFromCartChancePercent must be 0..100.")
            .Validate(static o => o.StatsIntervalSeconds > 0, "GroceryStoreStress:StatsIntervalSeconds must be > 0.")
            .Validate(static o => o.HotProductBiasPercent is >= 0 and <= 100, "GroceryStoreStress:HotProductBiasPercent must be 0..100.")
            .ValidateOnStart();
        services.AddOptions<PosSearchDemoOptions>()
            .Bind(context.Configuration.GetSection("PosSearchDemo"))
            .Validate(static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.SqlitePath), "PosSearchDemo:SqlitePath is required when enabled.")
            .Validate(static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.RedisIndexName), "PosSearchDemo:RedisIndexName is required when enabled.")
            .Validate(static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.RedisKeyPrefix), "PosSearchDemo:RedisKeyPrefix is required when enabled.")
            .Validate(static o => o.TopResults > 0 && o.TopResults <= 100, "PosSearchDemo:TopResults must be in range 1..100.")
            .Validate(static o => o.SeedProductCount >= 5, "PosSearchDemo:SeedProductCount must be >= 5.")
            .ValidateOnStart();
        services.AddOptions<PosSearchLoadOptions>()
            .Bind(context.Configuration.GetSection("PosSearchLoad"))
            .Validate(static o => !o.Enabled || o.Duration > TimeSpan.Zero, "PosSearchLoad:Duration must be > 0 when enabled.")
            .Validate(static o => !o.Enabled || o.Concurrency > 0, "PosSearchLoad:Concurrency must be > 0 when enabled.")
            .Validate(static o => !o.Enabled || o.LogEvery > TimeSpan.Zero, "PosSearchLoad:LogEvery must be > 0 when enabled.")
            .Validate(static o => !o.Enabled || o.TargetShoppersPerSecond >= 0, "PosSearchLoad:TargetShoppersPerSecond must be >= 0 when enabled.")
            .Validate(static o => !o.Enabled || !o.EnableAutoRamp || o.RampStepDuration > TimeSpan.Zero, "PosSearchLoad:RampStepDuration must be > 0 when auto ramp is enabled.")
            .Validate(static o => !o.Enabled || !o.EnableAutoRamp || !string.IsNullOrWhiteSpace(o.RampSteps), "PosSearchLoad:RampSteps is required when auto ramp is enabled.")
            .Validate(static o => !o.Enabled || o.MaxFailurePercent is >= 0 and <= 100, "PosSearchLoad:MaxFailurePercent must be in range 0..100 when enabled.")
            .Validate(static o => !o.Enabled || o.MaxP95Ms >= 0, "PosSearchLoad:MaxP95Ms must be >= 0 when enabled.")
            .Validate(static o => !o.Enabled || o.HotQueryPercent is >= 0 and <= 100, "PosSearchLoad:HotQueryPercent must be 0..100 when enabled.")
            .Validate(static o => !o.Enabled || o.CashierQueryPercent is >= 0 and <= 100, "PosSearchLoad:CashierQueryPercent must be 0..100 when enabled.")
            .Validate(static o => !o.Enabled || o.LookupUpcPercent is >= 0 and <= 100, "PosSearchLoad:LookupUpcPercent must be 0..100 when enabled.")
            .Validate(static o => !o.Enabled || (o.HotQueryPercent + o.CashierQueryPercent + o.LookupUpcPercent) <= 100, "PosSearchLoad percentages must sum to <= 100 when enabled.")
            .Validate(static o => !o.Enabled || o.LatencySampleSize >= 256, "PosSearchLoad:LatencySampleSize must be >= 256 when enabled.")
            .ValidateOnStart();

        services
            .AddOptions<StartupPreflightOptions>()
            .Bind(context.Configuration.GetSection("StartupPreflight"))
            .Validate(static o => !o.Enabled || o.Timeout >= TimeSpan.Zero, "StartupPreflight:Timeout must be >= 0.")
            .Validate(static o => !o.Enabled || o.Connections > 0, "StartupPreflight:Connections must be > 0 when enabled.")
            .ValidateOnStart();

        services
            .AddOptions<RedisConnectionOptions>()
            .Bind(context.Configuration.GetSection("RedisConnection"))
            .Validate(static o => !string.IsNullOrWhiteSpace(o.Host) || !string.IsNullOrWhiteSpace(o.ConnectionString), "RedisConnection:Host or RedisConnection:ConnectionString is required.")
            .Validate(static o => o.MaxConnections > 0, "RedisConnection:MaxConnections must be > 0.")
            .Validate(static o => o.MaxIdle > 0, "RedisConnection:MaxIdle must be > 0.")
            .Validate(static o => o.MaxIdle <= o.MaxConnections, "RedisConnection:MaxIdle must be <= MaxConnections.")
            .Validate(static o => o.Warm >= 0, "RedisConnection:Warm must be >= 0.")
            .Validate(static o => o.Warm <= o.MaxIdle, "RedisConnection:Warm must be <= MaxIdle.")
            .Validate(static o => o.ValidateAfterIdle >= TimeSpan.Zero, "RedisConnection:ValidateAfterIdle must be >= 0.")
            .Validate(static o => o.ValidateTimeout >= TimeSpan.Zero, "RedisConnection:ValidateTimeout must be >= 0.")
            .Validate(static o => o.IdleTimeout >= TimeSpan.Zero, "RedisConnection:IdleTimeout must be >= 0.")
            .Validate(static o => o.MaxConnectionLifetime >= TimeSpan.Zero, "RedisConnection:MaxConnectionLifetime must be >= 0.")
            .Validate(static o => o.ReaperPeriod >= TimeSpan.Zero, "RedisConnection:ReaperPeriod must be >= 0.")
            .Validate(static o => o.TcpSendBufferBytes >= 0, "RedisConnection:TcpSendBufferBytes must be >= 0.")
            .Validate(static o => o.TcpReceiveBufferBytes >= 0, "RedisConnection:TcpReceiveBufferBytes must be >= 0.")
            .Validate(static o => o.RespProtocolVersion is 2 or 3, "RedisConnection:RespProtocolVersion must be 2 or 3.")
            .Validate(static o => o.MaxClusterRedirects >= 0 && o.MaxClusterRedirects <= 16, "RedisConnection:MaxClusterRedirects must be in range 0..16.")
            .ValidateOnStart();

        services
            .AddOptions<RedisStressOptions>()
            .Bind(context.Configuration.GetSection("RedisStress"))
            .Validate(static o => !o.Enabled || o.Workers > 0, "RedisStress:Workers must be > 0.")
            .Validate(static o => !o.Enabled || (o.Mode ?? "pool").Trim().ToLowerInvariant() != "burn" || o.BurnConnectionsTarget > 0, "RedisStress:BurnConnectionsTarget must be > 0 when Mode=burn.")
            .Validate(static o => !o.Enabled || o.PayloadBytes >= 0, "RedisStress:PayloadBytes must be >= 0.")
            .Validate(static o => !o.Enabled || o.KeySpace > 0, "RedisStress:KeySpace must be > 0.")
            .Validate(static o => !o.Enabled || o.VirtualUsers > 0, "RedisStress:VirtualUsers must be > 0.")
            .Validate(static o => !o.Enabled || o.SetPercent is >= 0 and <= 100, "RedisStress:SetPercent must be 0..100.")
            .Validate(static o => !o.Enabled || o.PayloadTtl >= TimeSpan.Zero, "RedisStress:PayloadTtl must be >= 0.")
            .Validate(static o => !o.Enabled || o.TargetRps >= 0, "RedisStress:TargetRps must be >= 0.")
            .Validate(static o => !o.Enabled || o.BurstRequests > 0, "RedisStress:BurstRequests must be > 0.")
            .ValidateOnStart();

        services
            .AddOptions<RedisMultiplexerOptions>()
            .Bind(context.Configuration.GetSection("RedisMultiplexer"))
            .Validate(static o => o.Connections > 0, "RedisMultiplexer:Connections must be > 0.")
            .Validate(static o => o.MaxInFlightPerConnection > 0, "RedisMultiplexer:MaxInFlightPerConnection must be > 0.")
            .Validate(static o => o.CoalescedWriteMaxBytes > 0, "RedisMultiplexer:CoalescedWriteMaxBytes must be > 0.")
            .Validate(static o => o.CoalescedWriteMaxSegments > 0, "RedisMultiplexer:CoalescedWriteMaxSegments must be > 0.")
            .Validate(static o => o.CoalescedWriteSmallCopyThresholdBytes > 0, "RedisMultiplexer:CoalescedWriteSmallCopyThresholdBytes must be > 0.")
            .Validate(static o => o.AdaptiveCoalescingLowDepth > 0, "RedisMultiplexer:AdaptiveCoalescingLowDepth must be > 0.")
            .Validate(static o => o.AdaptiveCoalescingHighDepth > 0, "RedisMultiplexer:AdaptiveCoalescingHighDepth must be > 0.")
            .Validate(static o => o.AdaptiveCoalescingHighDepth >= o.AdaptiveCoalescingLowDepth, "RedisMultiplexer:AdaptiveCoalescingHighDepth must be >= AdaptiveCoalescingLowDepth.")
            .Validate(static o => o.AdaptiveCoalescingMinWriteBytes > 0, "RedisMultiplexer:AdaptiveCoalescingMinWriteBytes must be > 0.")
            .Validate(static o => o.AdaptiveCoalescingMinSegments > 0, "RedisMultiplexer:AdaptiveCoalescingMinSegments must be > 0.")
            .Validate(static o => o.AdaptiveCoalescingMinSmallCopyThresholdBytes > 0, "RedisMultiplexer:AdaptiveCoalescingMinSmallCopyThresholdBytes must be > 0.")
            .Validate(static o => o.MinConnections > 0, "RedisMultiplexer:MinConnections must be > 0.")
            .Validate(static o => o.MaxConnections > 0, "RedisMultiplexer:MaxConnections must be > 0.")
            .Validate(static o => o.MaxConnections >= o.MinConnections, "RedisMultiplexer:MaxConnections must be >= MinConnections.")
            .Validate(static o => o.AutoscaleSampleInterval > TimeSpan.Zero, "RedisMultiplexer:AutoscaleSampleInterval must be > 0.")
            .Validate(static o => o.ScaleUpWindow > TimeSpan.Zero, "RedisMultiplexer:ScaleUpWindow must be > 0.")
            .Validate(static o => o.ScaleDownWindow > TimeSpan.Zero, "RedisMultiplexer:ScaleDownWindow must be > 0.")
            .Validate(static o => o.ScaleUpCooldown > TimeSpan.Zero, "RedisMultiplexer:ScaleUpCooldown must be > 0.")
            .Validate(static o => o.ScaleDownCooldown > TimeSpan.Zero, "RedisMultiplexer:ScaleDownCooldown must be > 0.")
            .Validate(static o => o.ScaleUpInflightUtilization > 0 && o.ScaleUpInflightUtilization <= 1, "RedisMultiplexer:ScaleUpInflightUtilization must be in (0,1].")
            .Validate(static o => o.ScaleDownInflightUtilization >= 0 && o.ScaleDownInflightUtilization < 1, "RedisMultiplexer:ScaleDownInflightUtilization must be in [0,1).")
            .Validate(static o => o.ScaleUpQueueDepthThreshold > 0, "RedisMultiplexer:ScaleUpQueueDepthThreshold must be > 0.")
            .Validate(static o => o.ScaleUpTimeoutRatePerSecThreshold > 0, "RedisMultiplexer:ScaleUpTimeoutRatePerSecThreshold must be > 0.")
            .Validate(static o => o.ScaleUpP99LatencyMsThreshold > 0, "RedisMultiplexer:ScaleUpP99LatencyMsThreshold must be > 0.")
            .Validate(static o => o.ScaleDownP95LatencyMsThreshold > 0, "RedisMultiplexer:ScaleDownP95LatencyMsThreshold must be > 0.")
            .Validate(static o => o.BulkLaneConnections >= 0, "RedisMultiplexer:BulkLaneConnections must be >= 0.")
            .Validate(static o => !o.AutoAdjustBulkLanes || (o.BulkLaneTargetRatio >= 0 && o.BulkLaneTargetRatio <= 0.90), "RedisMultiplexer:BulkLaneTargetRatio must be in [0,0.90] when AutoAdjustBulkLanes is enabled.")
            .Validate(static o => o.EmergencyScaleUpTimeoutRatePerSecThreshold > 0, "RedisMultiplexer:EmergencyScaleUpTimeoutRatePerSecThreshold must be > 0.")
            .Validate(static o => o.ScaleDownDrainTimeout > TimeSpan.Zero, "RedisMultiplexer:ScaleDownDrainTimeout must be > 0.")
            .Validate(static o => o.MaxScaleEventsPerMinute > 0, "RedisMultiplexer:MaxScaleEventsPerMinute must be > 0.")
            .Validate(static o => o.FlapToggleThreshold >= 2, "RedisMultiplexer:FlapToggleThreshold must be >= 2.")
            .Validate(static o => o.AutoscaleFreezeDuration > TimeSpan.Zero, "RedisMultiplexer:AutoscaleFreezeDuration must be > 0.")
            .Validate(static o => o.ReconnectStormFailureRatePerSecThreshold > 0, "RedisMultiplexer:ReconnectStormFailureRatePerSecThreshold must be > 0.")
            .ValidateOnStart();

        services
            .AddOptions<CacheStampedeOptions>()
            .ConfigureCacheStampede(static options => ApplyCacheStampedeFluentDefaults(options))
            .ConfigureCacheStampede(options => options.UseProfile(ResolveCacheStampedeProfile(context.Configuration)))
            .Bind(context.Configuration.GetSection("CacheStampede"))
            .Validate(static o => o.MaxKeys > 0, "CacheStampede:MaxKeys must be > 0.")
            .Validate(static o => o.MaxKeys <= 500_000, "CacheStampede:MaxKeys must be <= 500000.")
            .Validate(static o => o.MaxKeyLength > 0, "CacheStampede:MaxKeyLength must be > 0.")
            .Validate(static o => o.MaxKeyLength <= 4096, "CacheStampede:MaxKeyLength must be <= 4096.")
            .Validate(static o => o.LockWaitTimeout >= TimeSpan.Zero, "CacheStampede:LockWaitTimeout must be >= 0.")
            .Validate(static o => o.LockWaitTimeout <= TimeSpan.FromSeconds(30), "CacheStampede:LockWaitTimeout must be <= 30 seconds.")
            .Validate(static o => o.FailureBackoff >= TimeSpan.Zero, "CacheStampede:FailureBackoff must be >= 0.")
            .Validate(static o => o.FailureBackoff <= TimeSpan.FromSeconds(30), "CacheStampede:FailureBackoff must be <= 30 seconds.")
            .ValidateOnStart();

        services
            .AddOptions<RedisCircuitBreakerOptions>()
            .Bind(context.Configuration.GetSection("RedisCircuitBreaker"))
            .Validate(static o => o.ConsecutiveFailuresToOpen > 0, "RedisCircuitBreaker:ConsecutiveFailuresToOpen must be > 0.")
            .Validate(static o => o.BreakDuration >= TimeSpan.Zero, "RedisCircuitBreaker:BreakDuration must be >= 0.")
            .Validate(static o => o.HalfOpenProbeTimeout >= TimeSpan.Zero, "RedisCircuitBreaker:HalfOpenProbeTimeout must be >= 0.")
            .ValidateOnStart();

        services
            .AddOptions<HybridFailoverOptions>()
            .Bind(context.Configuration.GetSection("HybridFailover"))
            .Validate(static o => o.FallbackWarmReadTtl > TimeSpan.Zero, "HybridFailover:FallbackWarmReadTtl must be > 0.")
            .Validate(static o => o.FallbackMirrorWriteTtlWhenMissing > TimeSpan.Zero, "HybridFailover:FallbackMirrorWriteTtlWhenMissing must be > 0.")
            .Validate(static o => o.MaxMirrorPayloadBytes >= 0, "HybridFailover:MaxMirrorPayloadBytes must be >= 0.")
            .ValidateOnStart();

        if (context.HostingEnvironment.IsDevelopment())
        {
            services.AddVapeCacheRedisReconciliation(context.Configuration);
        }


        // Grocery Store Demo Services
        services.AddSingleton<VapeCache.Console.GroceryStore.GroceryStoreService>();
        services.AddHostedService<VapeCache.Console.GroceryStore.GroceryStoreStressTest>();
        services.AddSingleton<SqlitePosCatalogStore>();
        services.AddSingleton<PosCatalogSearchService>();
        services.AddHostedService<PosSearchDemoHostedService>();
        services.AddHostedService<PosSearchLoadHostedService>();
        services.AddSingleton<IVapeCachePlugin, SampleCatalogPlugin>();

        services.AddHostedService<StartupPreflightHostedService>();
        services.AddHostedService<RedisSanityCheckHostedService>();
        services.AddHostedService<RedisConnectionPoolReaperHostedService>();
        services.AddHostedService<SharedDashboardSnapshotPublisherHostedService>();
        services.AddHostedService<PluginDemoHostedService>();
    })
    .ConfigureContainer<ContainerBuilder>(static (context, builder) =>
    {
        builder.RegisterModule(new ConfigurationModule(context.Configuration));
        builder.RegisterModule(new VapeCacheConnectionsModule());
        builder.RegisterModule(new VapeCacheCachingModule());
    });

// Check if running in comparison mode
var runComparison = Environment.GetEnvironmentVariable("VAPECACHE_RUN_COMPARISON")?.ToLowerInvariant() == "true"
    || args.Contains("--compare", StringComparer.OrdinalIgnoreCase);

if (runComparison)
{
    // Run comparison mode instead of hosted service mode
    var tempConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    await VapeCache.Console.GroceryStore.MenuRunner.RunAsync(tempConfig);
    return;
}

using var host = hostBuilder.Build();

var lifecycleLogger = host.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("VapeCache.Console.Lifecycle");

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    lifecycleLogger.LogInformation("VapeCache.Console started. Press Ctrl+C to exit.");
});
lifetime.ApplicationStopping.Register(() =>
{
    lifecycleLogger.LogInformation("VapeCache.Console stopping...");
});
lifetime.ApplicationStopped.Register(() =>
{
    lifecycleLogger.LogInformation("VapeCache.Console stopped.");
});

try
{
    await host.StartAsync().ConfigureAwait(false);
    await host.WaitForShutdownAsync().ConfigureAwait(false);
}
finally
{
    try { await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
}

static string? ResolveOtlpEndpoint(IConfiguration configuration)
{
    var endpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
    if (!string.IsNullOrWhiteSpace(endpoint))
        return endpoint;

    endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
}

static void ConfigureOtlpForSignal(string endpoint, OtlpExporterOptions otlp, string signal)
{
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        return;

    var configuredProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
    if (TryParseOtlpProtocol(configuredProtocol, out var explicitProtocol))
    {
        if (explicitProtocol == OtlpExportProtocol.HttpProtobuf)
        {
            otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            otlp.Endpoint = ResolveSignalEndpoint(endpointUri, signal);
            return;
        }

        otlp.Protocol = OtlpExportProtocol.Grpc;
        otlp.Endpoint = endpointUri;
        return;
    }

    var isHttpProtobuf = endpointUri.Port == 5341 ||
                         endpointUri.Port == 4318 ||
                         endpointUri.AbsolutePath.Contains("/ingest/otlp", StringComparison.OrdinalIgnoreCase) ||
                         endpointUri.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase);

    if (isHttpProtobuf)
    {
        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
        otlp.Endpoint = ResolveSignalEndpoint(endpointUri, signal);
        return;
    }

    otlp.Protocol = OtlpExportProtocol.Grpc;
    otlp.Endpoint = endpointUri;
}

static Uri ResolveSignalEndpoint(Uri endpoint, string signal)
{
    var endpointText = endpoint.ToString().TrimEnd('/');
    var signalSuffix = $"/v1/{signal}";

    if (endpointText.EndsWith(signalSuffix, StringComparison.OrdinalIgnoreCase))
        return endpoint;

    if (endpointText.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        return new Uri($"{endpointText}/{signal}", UriKind.Absolute);

    if (endpointText.EndsWith("/ingest/otlp", StringComparison.OrdinalIgnoreCase))
        return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);

    if (endpointText.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
        return endpoint;

    return new Uri($"{endpointText}{signalSuffix}", UriKind.Absolute);
}

static bool TryParseOtlpProtocol(string? configured, out OtlpExportProtocol protocol)
{
    protocol = default;
    if (string.IsNullOrWhiteSpace(configured))
        return false;

    var normalized = configured.Trim().ToLowerInvariant();
    if (normalized is "http/protobuf" or "http-protobuf" or "httpprotobuf")
    {
        protocol = OtlpExportProtocol.HttpProtobuf;
        return true;
    }

    if (normalized is "grpc")
    {
        protocol = OtlpExportProtocol.Grpc;
        return true;
    }

    return Enum.TryParse(configured, ignoreCase: true, out protocol);
}

static CacheStampedeProfile ResolveCacheStampedeProfile(IConfiguration configuration)
{
    var configured = configuration["CacheStampede:Profile"];
    if (string.IsNullOrWhiteSpace(configured))
        return CacheStampedeProfile.Balanced;

    return Enum.TryParse(configured, ignoreCase: true, out CacheStampedeProfile parsed) &&
           Enum.IsDefined(parsed)
        ? parsed
        : CacheStampedeProfile.Balanced;
}

static void ApplyCacheStampedeFluentDefaults(CacheStampedeOptionsBuilder options)
{
    options.UseProfile(CacheStampedeProfile.Balanced)
        .Enabled()
        .RejectSuspiciousKeys()
        .EnableFailureBackoff()
        .WithMaxKeys(50_000)
        .WithMaxKeyLength(512)
        .WithLockWaitTimeout(TimeSpan.FromMilliseconds(750))
        .WithFailureBackoff(TimeSpan.FromMilliseconds(500));
}
