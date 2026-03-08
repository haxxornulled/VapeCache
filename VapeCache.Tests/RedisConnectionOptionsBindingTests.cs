using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Tests;

public sealed class RedisConnectionOptionsBindingTests
{
    [Fact]
    public void Options_bind_from_configuration()
    {
        var json = """
        {
            "RedisConnection": {
            "ConnectionString": null,
            "Host": "localhost",
            "Port": 6380,
            "Username": "u",
            "Password": "pw",
            "Database": 3,
            "UseTls": true,
            "TlsHost": "redis.example",
            "AllowInvalidCert": true,
            "MaxConnections": 7,
            "MaxIdle": 6,
            "Warm": 2,
            "TransportProfile": "Balanced",
            "EnableTcpNoDelay": false,
            "TcpSendBufferBytes": 262144,
            "TcpReceiveBufferBytes": 524288,
            "MaxBulkStringBytes": 8388608,
            "MaxArrayDepth": 32,
            "RespProtocolVersion": 3,
            "EnableClusterRedirection": true,
            "MaxClusterRedirects": 7,
            "ConnectTimeout": "00:00:05",
            "AcquireTimeout": "00:00:06"
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services
            .AddOptions<RedisConnectionOptions>()
            .Bind(config.GetSection("RedisConnection"));

        using var sp = services.BuildServiceProvider();
        var o = sp.GetRequiredService<IOptions<RedisConnectionOptions>>().Value;

        Assert.Equal("localhost", o.Host);
        Assert.Equal(6380, o.Port);
        Assert.Equal("u", o.Username);
        Assert.Equal("pw", o.Password);
        Assert.Equal(3, o.Database);
        Assert.True(o.UseTls);
        Assert.Equal("redis.example", o.TlsHost);
        Assert.True(o.AllowInvalidCert);
        Assert.Equal(7, o.MaxConnections);
        Assert.Equal(6, o.MaxIdle);
        Assert.Equal(2, o.Warm);
        Assert.Equal(RedisTransportProfile.Balanced, o.TransportProfile);
        Assert.Equal(TimeSpan.FromSeconds(5), o.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(6), o.AcquireTimeout);
        Assert.False(o.EnableTcpNoDelay);
        Assert.Equal(262144, o.TcpSendBufferBytes);
        Assert.Equal(524288, o.TcpReceiveBufferBytes);
        Assert.Equal(8 * 1024 * 1024, o.MaxBulkStringBytes);
        Assert.Equal(32, o.MaxArrayDepth);
        Assert.Equal(3, o.RespProtocolVersion);
        Assert.True(o.EnableClusterRedirection);
        Assert.Equal(7, o.MaxClusterRedirects);
    }
}
