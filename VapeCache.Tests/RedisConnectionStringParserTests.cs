using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionStringParserTests
{
    [Fact]
    public void Parses_basic_redis_uri()
    {
        var ok = RedisConnectionStringParser.TryParse("redis://localhost:6380/2", out var parsed, out var err);
        Assert.True(ok, err);
        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(6380, parsed.Port);
        Assert.Equal(2, parsed.Database);
        Assert.False(parsed.UseTls);
    }

    [Fact]
    public void Defaults_port_and_database()
    {
        var ok = RedisConnectionStringParser.TryParse("redis://localhost", out var parsed, out var err);
        Assert.True(ok, err);
        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(6379, parsed.Port);
        Assert.Equal(0, parsed.Database);
    }

    [Fact]
    public void Parses_password_only_userinfo()
    {
        var ok = RedisConnectionStringParser.TryParse("redis://:pw@localhost:6379/0", out var parsed, out var err);
        Assert.True(ok, err);
        Assert.Null(parsed.Username);
        Assert.Equal("pw", parsed.Password);
    }

    [Fact]
    public void Rejects_invalid_database()
    {
        var ok = RedisConnectionStringParser.TryParse("redis://localhost/notanint", out _, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public void Rejects_unsupported_scheme()
    {
        var ok = RedisConnectionStringParser.TryParse("http://localhost:6379/0", out _, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public void Parses_rediss_with_username_password_sni_and_allow_invalid()
    {
        var ok = RedisConnectionStringParser.TryParse("rediss://u:p%40ss%21%21@cache.local:6379/0?allowInvalidCert=true&sni=sni.host", out var parsed, out var err);
        Assert.True(ok, err);
        Assert.Equal("cache.local", parsed.Host);
        Assert.Equal(6379, parsed.Port);
        Assert.Equal("u", parsed.Username);
        Assert.Equal("p@ss!!", parsed.Password);
        Assert.True(parsed.UseTls);
        Assert.Equal("sni.host", parsed.TlsHost);
        Assert.True(parsed.AllowInvalidCert);
    }

    [Fact]
    public void Query_can_force_tls_on()
    {
        var ok = RedisConnectionStringParser.TryParse("redis://localhost:6379/0?tls=true", out var parsed, out var err);
        Assert.True(ok, err);
        Assert.True(parsed.UseTls);
    }
}
