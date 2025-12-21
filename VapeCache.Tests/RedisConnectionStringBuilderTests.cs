using VapeCache.Application.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionStringBuilderTests
{
    [Fact]
    public void Builds_basic_redis_uri()
    {
        var builder = new RedisConnectionStringBuilder();
        var uri = builder.Build(new RedisConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 6379,
            Database = 0
        });

        Assert.Equal("redis://127.0.0.1:6379/0", uri);
    }

    [Fact]
    public void Builds_rediss_uri_with_password_and_sni_and_allow_invalid_cert()
    {
        var builder = new RedisConnectionStringBuilder();
        var uri = builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            Port = 6380,
            Password = "p@ ss",
            Database = 2,
            UseTls = true,
            TlsHost = "sni.host",
            AllowInvalidCert = true
        });

        Assert.Equal("rediss://:p%40%20ss@cache.local:6380/2?tls=true&sni=sni.host&allowInvalidCert=true", uri);
    }

    [Fact]
    public void Builds_rediss_uri_with_username_and_password()
    {
        var builder = new RedisConnectionStringBuilder();
        var uri = builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            Username = "dfwredis",
            Password = "p@ss!!",
            UseTls = true
        });

        Assert.Equal("rediss://dfwredis:p%40ss%21%21@cache.local:6379/0?tls=true", uri);
    }
}
