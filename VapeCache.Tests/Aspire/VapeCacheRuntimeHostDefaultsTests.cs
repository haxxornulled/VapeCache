using Microsoft.Extensions.Configuration;
using VapeCache.Extensions.Aspire.Hosting;

namespace VapeCache.Tests.Aspire;

public sealed class VapeCacheRuntimeHostDefaultsTests
{
    [Fact]
    public void ApplyRedisMultiplexerDefaults_FillsMissingValues()
    {
        var configuration = new ConfigurationManager();

        VapeCacheRuntimeHostDefaults.ApplyRedisMultiplexerDefaults(configuration);

        Assert.Equal("64", configuration["RedisMultiplexer:Connections"]);
        Assert.Equal("true", configuration["RedisMultiplexer:EnableAutoscaling"]);
        Assert.Equal("16", configuration["RedisMultiplexer:MinConnections"]);
        Assert.Equal("64", configuration["RedisMultiplexer:MaxConnections"]);
        Assert.Equal("16", configuration["RedisMultiplexer:BulkLaneConnections"]);
        Assert.Equal("true", configuration["RedisMultiplexer:AutoAdjustBulkLanes"]);
        Assert.Equal("0.25", configuration["RedisMultiplexer:BulkLaneTargetRatio"]);
    }

    [Fact]
    public void ApplyRedisMultiplexerDefaults_DoesNotOverrideExplicitValues()
    {
        var configuration = new ConfigurationManager();
        configuration["RedisMultiplexer:Connections"] = "28";
        configuration["RedisMultiplexer:BulkLaneConnections"] = "4";

        VapeCacheRuntimeHostDefaults.ApplyRedisMultiplexerDefaults(configuration);

        Assert.Equal("28", configuration["RedisMultiplexer:Connections"]);
        Assert.Equal("4", configuration["RedisMultiplexer:BulkLaneConnections"]);
        Assert.Equal("16", configuration["RedisMultiplexer:MinConnections"]);
    }
}
