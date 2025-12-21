using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionFactoryResolveOptionsTests
{
    [Fact]
    public void ResolveOptions_returns_original_when_connection_string_empty()
    {
        var o = new RedisConnectionOptions
        {
            Host = "h",
            MaxConnections = 10,
            MaxIdle = 5
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);
        Assert.Equal(o, effective);
    }

    [Fact]
    public void ResolveOptions_returns_original_when_connection_string_invalid()
    {
        var o = new RedisConnectionOptions
        {
            ConnectionString = "not a uri",
            Host = "h",
            Port = 1234,
            UseTls = false
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);
        Assert.Equal("h", effective.Host);
        Assert.Equal(1234, effective.Port);
        Assert.False(effective.UseTls);
    }

    [Fact]
    public void ResolveOptions_overrides_endpoint_and_auth_but_keeps_pooling_values()
    {
        var o = new RedisConnectionOptions
        {
            ConnectionString = "rediss://u:p%40ss%21%21@cache.local:6380/2?sni=sni.host&allowInvalidCert=true",
            Host = "ignored",
            Port = 1,
            Username = "ignored",
            Password = "ignored",
            Database = 0,
            UseTls = false,
            TlsHost = null,
            AllowInvalidCert = false,

            MaxConnections = 200,
            MaxIdle = 50,
            Warm = 10,
            AcquireTimeout = TimeSpan.FromSeconds(1),
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };

        var effective = RedisConnectionFactory.ResolveOptions(o);

        Assert.Equal("cache.local", effective.Host);
        Assert.Equal(6380, effective.Port);
        Assert.Equal("u", effective.Username);
        Assert.Equal("p@ss!!", effective.Password);
        Assert.Equal(2, effective.Database);
        Assert.True(effective.UseTls);
        Assert.Equal("sni.host", effective.TlsHost);
        Assert.True(effective.AllowInvalidCert);

        Assert.Equal(200, effective.MaxConnections);
        Assert.Equal(50, effective.MaxIdle);
        Assert.Equal(10, effective.Warm);
        Assert.Equal(TimeSpan.FromSeconds(1), effective.AcquireTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), effective.ConnectTimeout);
    }
}
