using VapeCache.Abstractions.Connections;

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

    [Fact]
    public void Builds_ipv6_host_without_brackets()
    {
        var builder = new RedisConnectionStringBuilder();
        var uri = builder.Build(new RedisConnectionOptions
        {
            Host = "2001:db8::1",
            Port = 6380,
            Database = 4
        });

        Assert.Equal("redis://[2001:db8::1]:6380/4", uri);
    }

    [Fact]
    public void Keeps_ipv6_host_with_brackets()
    {
        var builder = new RedisConnectionStringBuilder();
        var uri = builder.Build(new RedisConnectionOptions
        {
            Host = "[2001:db8::1]",
            Port = 6380,
            Database = 4
        });

        Assert.Equal("redis://[2001:db8::1]:6380/4", uri);
    }

    [Fact]
    public void Throws_when_host_contains_port()
    {
        var builder = new RedisConnectionStringBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local:6380"
        }));

        Assert.Contains("Host should not include a port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_host_contains_uri_scheme()
    {
        var builder = new RedisConnectionStringBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Build(new RedisConnectionOptions
        {
            Host = "redis://cache.local"
        }));

        Assert.Contains("Host should not include a URI scheme", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_tls_host_is_set_without_tls()
    {
        var builder = new RedisConnectionStringBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            TlsHost = "sni.cache.local",
            UseTls = false
        }));

        Assert.Contains("TlsHost requires UseTls=true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_allow_invalid_cert_is_set_without_tls()
    {
        var builder = new RedisConnectionStringBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            AllowInvalidCert = true,
            UseTls = false
        }));

        Assert.Contains("AllowInvalidCert requires UseTls=true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_username_is_set_without_password()
    {
        var builder = new RedisConnectionStringBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            Username = "svc-redis"
        }));

        Assert.Contains("Username requires a non-empty Password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_roundtrips_through_parser_for_tls_options()
    {
        var builder = new RedisConnectionStringBuilder();
        var built = builder.Build(new RedisConnectionOptions
        {
            Host = "cache.local",
            Port = 6380,
            Username = "svc-redis",
            Password = "p@ ss",
            Database = 6,
            UseTls = true,
            TlsHost = "redis-sni.internal",
            AllowInvalidCert = true
        });

        var ok = RedisConnectionStringParser.TryParse(built, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal("cache.local", parsed.Host);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal("svc-redis", parsed.Username);
        Assert.Equal("p@ ss", parsed.Password);
        Assert.Equal(6, parsed.Database);
        Assert.True(parsed.UseTls);
        Assert.Equal("redis-sni.internal", parsed.TlsHost);
        Assert.True(parsed.AllowInvalidCert);
    }
}
