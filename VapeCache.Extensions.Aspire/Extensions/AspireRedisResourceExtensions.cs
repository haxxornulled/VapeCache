using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Represents the aspire redis resource extensions.
/// </summary>
public static class AspireRedisResourceExtensions
{
    private static readonly PropertyInfo ConnectionStringProperty =
        typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.ConnectionString))!;

    /// <summary>
    /// Configures value.
    /// </summary>
    public static AspireVapeCacheBuilder WithRedisFromAspire(
        this AspireVapeCacheBuilder builder,
        string connectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        var redisSection = builder.Builder.Configuration.GetSection("RedisConnection");
        builder.Builder.Services.AddOptions<RedisConnectionOptions>().Bind(redisSection);

        var connectionStringKey = $"ConnectionStrings:{connectionName}";
        builder.Builder.Services.PostConfigure<RedisConnectionOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                return;

            var aspireConnectionString = builder.Builder.Configuration[connectionStringKey];
            if (string.IsNullOrWhiteSpace(aspireConnectionString))
                return;

            ConnectionStringProperty.SetValue(options, aspireConnectionString);
        });

        return builder;
    }
}
