using System.Globalization;

namespace VapeCache.Extensions.Aspire;

internal static class RedisExporterMetricsParser
{
    public static bool TryParse(string payload, out RedisExporterMetricValues values)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            values = default;
            return false;
        }

        var span = payload.AsSpan();
        var parsedAny = false;
        var exporterUp = 0;
        var connectedClients = 0d;
        var blockedClients = 0d;
        var opsPerSecond = 0d;
        var usedMemoryBytes = 0d;
        var maxMemoryBytes = 0d;
        long commandsProcessedTotal = 0L;
        long keyspaceHitsTotal = 0L;
        long keyspaceMissesTotal = 0L;
        long evictedKeysTotal = 0L;
        long netInputBytesTotal = 0L;
        long netOutputBytesTotal = 0L;

        while (!span.IsEmpty)
        {
            var lineBreak = span.IndexOf('\n');
            var line = lineBreak >= 0 ? span[..lineBreak] : span;
            if (!line.IsEmpty && line[^1] == '\r')
                line = line[..^1];

            if (TryParseLine(line, out var metricName, out var metricValue))
            {
                if (MetricEquals(metricName, "redis_up"))
                {
                    if (metricValue > 0d)
                        exporterUp = 1;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_connected_clients"))
                {
                    connectedClients += metricValue;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_blocked_clients"))
                {
                    blockedClients += metricValue;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_instantaneous_ops_per_sec"))
                {
                    opsPerSecond += metricValue;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_memory_used_bytes"))
                {
                    usedMemoryBytes += metricValue;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_memory_max_bytes"))
                {
                    maxMemoryBytes += metricValue;
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_commands_processed_total"))
                {
                    commandsProcessedTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_keyspace_hits_total"))
                {
                    keyspaceHitsTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_keyspace_misses_total"))
                {
                    keyspaceMissesTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_evicted_keys_total"))
                {
                    evictedKeysTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_net_input_bytes_total"))
                {
                    netInputBytesTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
                else if (MetricEquals(metricName, "redis_net_output_bytes_total"))
                {
                    netOutputBytesTotal += ClampToLong(metricValue);
                    parsedAny = true;
                }
            }

            if (lineBreak < 0)
                break;

            span = span[(lineBreak + 1)..];
        }

        if (!parsedAny)
        {
            values = default;
            return false;
        }

        values = new RedisExporterMetricValues(
            ExporterUp: exporterUp,
            ConnectedClients: connectedClients,
            BlockedClients: blockedClients,
            OpsPerSecond: opsPerSecond,
            UsedMemoryBytes: usedMemoryBytes,
            MaxMemoryBytes: maxMemoryBytes,
            CommandsProcessedTotal: commandsProcessedTotal,
            KeyspaceHitsTotal: keyspaceHitsTotal,
            KeyspaceMissesTotal: keyspaceMissesTotal,
            EvictedKeysTotal: evictedKeysTotal,
            NetInputBytesTotal: netInputBytesTotal,
            NetOutputBytesTotal: netOutputBytesTotal);
        return true;
    }

    private static bool TryParseLine(
        ReadOnlySpan<char> line,
        out ReadOnlySpan<char> metricName,
        out double metricValue)
    {
        metricName = default;
        metricValue = 0d;

        line = line.Trim();
        if (line.IsEmpty || line[0] == '#')
            return false;

        var metricTokenEnd = line.IndexOfAny(' ', '\t');
        if (metricTokenEnd <= 0)
            return false;

        var metricToken = line[..metricTokenEnd];
        var valueToken = line[(metricTokenEnd + 1)..].TrimStart();
        if (valueToken.IsEmpty)
            return false;

        var valueTokenEnd = valueToken.IndexOfAny(' ', '\t');
        if (valueTokenEnd > 0)
            valueToken = valueToken[..valueTokenEnd];

        var labelsStart = metricToken.IndexOf('{');
        metricName = labelsStart > 0 ? metricToken[..labelsStart] : metricToken;
        if (metricName.IsEmpty)
            return false;

        return double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out metricValue);
    }

    private static bool MetricEquals(ReadOnlySpan<char> metricName, string expected)
        => metricName.Equals(expected.AsSpan(), StringComparison.Ordinal);

    private static long ClampToLong(double value)
    {
        if (value <= 0d)
            return 0L;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return (long)value;
    }
}
