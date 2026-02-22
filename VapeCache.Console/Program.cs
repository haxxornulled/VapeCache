using Autofac;
using Autofac.Configuration;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Hosting;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.DependencyInjection;
using VapeCache.Console.Stress;
using VapeCache.Console.Secrets;
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
        // Never throw here; Redis can be unavailable or not configured. Startup preflight/failover controls behavior.
    })
    .UseSerilog(static (context, services, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();
    })
    .ConfigureServices(static (context, services) =>
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: "VapeCache.Console"))
            .WithMetrics(m =>
            {
                m.AddMeter("VapeCache.Redis");
                m.AddMeter("VapeCache.Cache");
                m.AddRuntimeInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddAspNetCoreInstrumentation();

                m.AddOtlpExporter(otlp =>
                {
                    ConfigureOtlpForSignal(context.Configuration, otlp, signal: "metrics");
                });
            })
            .WithTracing(t =>
            {
                t.AddSource("VapeCache.Redis");
                t.AddHttpClientInstrumentation();
                t.AddAspNetCoreInstrumentation();

                t.AddOtlpExporter(otlp =>
                {
                    ConfigureOtlpForSignal(context.Configuration, otlp, signal: "traces");
                });
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
            .ValidateOnStart();

        services
            .AddOptions<CacheStampedeOptions>()
            .Bind(context.Configuration.GetSection("CacheStampede"))
            .Validate(static o => o.MaxKeys > 0, "CacheStampede:MaxKeys must be > 0.")
            .ValidateOnStart();

        services
            .AddOptions<RedisCircuitBreakerOptions>()
            .Bind(context.Configuration.GetSection("RedisCircuitBreaker"))
            .Validate(static o => o.ConsecutiveFailuresToOpen > 0, "RedisCircuitBreaker:ConsecutiveFailuresToOpen must be > 0.")
            .Validate(static o => o.BreakDuration >= TimeSpan.Zero, "RedisCircuitBreaker:BreakDuration must be >= 0.")
            .Validate(static o => o.HalfOpenProbeTimeout >= TimeSpan.Zero, "RedisCircuitBreaker:HalfOpenProbeTimeout must be >= 0.")
            .ValidateOnStart();

        if (context.HostingEnvironment.IsDevelopment())
        {
            services.AddVapeCacheRedisReconciliation(context.Configuration);
        }


        // Grocery Store Demo Services
        services.AddSingleton<VapeCache.Console.GroceryStore.GroceryStoreService>();
        services.AddHostedService<VapeCache.Console.GroceryStore.GroceryStoreStressTest>();

        services.AddHostedService<StartupPreflightHostedService>();
        services.AddHostedService<RedisSanityCheckHostedService>();
        services.AddHostedService<RedisConnectionPoolReaperHostedService>();
        // services.AddHostedService<RedisStressHostedService>();  // Disabled in favor of grocery store test
        // services.AddHostedService<LiveDemoHostedService>();     // Disabled in favor of grocery store test
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

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    Log.Information("VapeCache.Console started. Press Ctrl+C to exit.");
});
lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("VapeCache.Console stopping...");
});
lifetime.ApplicationStopped.Register(() =>
{
    Log.Information("VapeCache.Console stopped.");
});

try
{
    await host.StartAsync().ConfigureAwait(false);
    await host.WaitForShutdownAsync().ConfigureAwait(false);
}
finally
{
    try { await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
    Log.CloseAndFlush();
}

static void ConfigureOtlpForSignal(IConfiguration configuration, OtlpExporterOptions otlp, string signal)
{
    var endpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    }

    // Default to Seq OTLP ingestion when no endpoint is configured.
    endpoint ??= "http://localhost:5341/ingest/otlp";

    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        return;

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
