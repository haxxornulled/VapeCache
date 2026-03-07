using VapeCache.Extensions.Aspire;

namespace VapeCache.Tests.Aspire;

public sealed class RedisExporterMetricsParserTests
{
    [Fact]
    public void TryParse_ReturnsFalse_WhenPayloadIsEmpty()
    {
        var parsed = RedisExporterMetricsParser.TryParse(string.Empty, out var values);

        Assert.False(parsed);
        Assert.Equal(default, values);
    }

    [Fact]
    public void TryParse_ExtractsRedisServerMetrics()
    {
        const string payload = """
            # HELP redis_up Information about the Redis instance
            redis_up{instance="redis:6379"} 1
            redis_connected_clients{instance="redis:6379"} 42
            redis_blocked_clients{instance="redis:6379"} 3
            redis_instantaneous_ops_per_sec{instance="redis:6379"} 1567
            redis_memory_used_bytes{instance="redis:6379"} 10485760
            redis_memory_max_bytes{instance="redis:6379"} 67108864
            redis_commands_processed_total{instance="redis:6379"} 998877
            redis_keyspace_hits_total{instance="redis:6379"} 500
            redis_keyspace_misses_total{instance="redis:6379"} 125
            redis_evicted_keys_total{instance="redis:6379"} 4
            redis_net_input_bytes_total{instance="redis:6379"} 4444
            redis_net_output_bytes_total{instance="redis:6379"} 8888
            """;

        var parsed = RedisExporterMetricsParser.TryParse(payload, out var values);

        Assert.True(parsed);
        Assert.Equal(1, values.ExporterUp);
        Assert.Equal(42d, values.ConnectedClients);
        Assert.Equal(3d, values.BlockedClients);
        Assert.Equal(1567d, values.OpsPerSecond);
        Assert.Equal(10485760d, values.UsedMemoryBytes);
        Assert.Equal(67108864d, values.MaxMemoryBytes);
        Assert.Equal(998877L, values.CommandsProcessedTotal);
        Assert.Equal(500L, values.KeyspaceHitsTotal);
        Assert.Equal(125L, values.KeyspaceMissesTotal);
        Assert.Equal(4L, values.EvictedKeysTotal);
        Assert.Equal(4444L, values.NetInputBytesTotal);
        Assert.Equal(8888L, values.NetOutputBytesTotal);
    }

    [Fact]
    public void TryParse_AggregatesMatchingSeries()
    {
        const string payload = """
            redis_connected_clients{instance="a"} 10
            redis_connected_clients{instance="b"} 12
            redis_commands_processed_total{instance="a"} 1000
            redis_commands_processed_total{instance="b"} 4000
            redis_up{instance="a"} 1
            redis_up{instance="b"} 0
            """;

        var parsed = RedisExporterMetricsParser.TryParse(payload, out var values);

        Assert.True(parsed);
        Assert.Equal(1, values.ExporterUp);
        Assert.Equal(22d, values.ConnectedClients);
        Assert.Equal(5000L, values.CommandsProcessedTotal);
    }
}
