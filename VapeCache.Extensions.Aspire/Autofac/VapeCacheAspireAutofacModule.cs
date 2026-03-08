using Autofac;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.DependencyInjection;

namespace VapeCache.Extensions.Aspire.Autofac;

/// <summary>
/// Autofac module composition for Aspire-hosted VapeCache applications.
/// </summary>
public sealed class VapeCacheAspireAutofacModule : Module
{
    private readonly IConfiguration _configuration;
    private readonly RedisTransportProfile _transportProfile;
    private readonly string _connectionName;

    /// <summary>
    /// Executes vape cache aspire autofac module.
    /// </summary>
    public VapeCacheAspireAutofacModule(
        IConfiguration configuration,
        RedisTransportProfile transportProfile = RedisTransportProfile.FullTilt,
        string connectionName = "redis")
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _transportProfile = transportProfile;
        _connectionName = string.IsNullOrWhiteSpace(connectionName) ? "redis" : connectionName.Trim();
    }

    /// <summary>
    /// Executes load.
    /// </summary>
    protected override void Load(ContainerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Register(_ => new ConfigurationReloadingOptionsMonitor<RedisConnectionOptions>(
                _configuration,
                () => BindConnectionOptions(_configuration, _connectionName) with
                {
                    TransportProfile = _transportProfile
                }))
            .As<IOptions<RedisConnectionOptions>>()
            .As<IOptionsMonitor<RedisConnectionOptions>>()
            .SingleInstance();

        builder.Register(_ => new ConfigurationReloadingOptionsMonitor<RedisMultiplexerOptions>(
                _configuration,
                () => BindMultiplexerOptions(_configuration) with
                {
                    TransportProfile = _transportProfile
                }))
            .As<IOptions<RedisMultiplexerOptions>>()
            .As<IOptionsMonitor<RedisMultiplexerOptions>>()
            .SingleInstance();

        builder.RegisterModule(new VapeCacheConnectionsModule());
        builder.RegisterModule(new VapeCacheCachingModule());
    }

    private static RedisConnectionOptions BindConnectionOptions(IConfiguration configuration, string connectionName)
    {
        var bound = new RedisConnectionOptions();
        configuration.GetSection("RedisConnection").Bind(bound);
        if (string.IsNullOrWhiteSpace(bound.ConnectionString))
        {
            var aspireConnectionString = configuration.GetConnectionString(connectionName);
            if (!string.IsNullOrWhiteSpace(aspireConnectionString))
                bound = bound with { ConnectionString = aspireConnectionString };
        }
        return bound;
    }

    private static RedisMultiplexerOptions BindMultiplexerOptions(IConfiguration configuration)
    {
        var bound = new RedisMultiplexerOptions();
        configuration.GetSection("RedisMultiplexer").Bind(bound);
        return bound;
    }
}
