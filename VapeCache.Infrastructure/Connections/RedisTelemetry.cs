using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VapeCache.Infrastructure.Connections;

public static class RedisTelemetry
{
    public static readonly ActivitySource ActivitySource = new("VapeCache.Redis");
    public static readonly Meter Meter = new("VapeCache.Redis");

    public static readonly Counter<long> ConnectAttempts = Meter.CreateCounter<long>("redis.connect.attempts");
    public static readonly Counter<long> ConnectFailures = Meter.CreateCounter<long>("redis.connect.failures");
    public static readonly Histogram<double> ConnectMs = Meter.CreateHistogram<double>("redis.connect.ms");

    public static readonly Counter<long> PoolAcquires = Meter.CreateCounter<long>("redis.pool.acquires");
    public static readonly Counter<long> PoolTimeouts = Meter.CreateCounter<long>("redis.pool.timeouts");
    public static readonly Histogram<double> PoolWaitMs = Meter.CreateHistogram<double>("redis.pool.wait.ms");
    public static readonly Counter<long> PoolDrops = Meter.CreateCounter<long>("redis.pool.drops");
    public static readonly Counter<long> PoolReaps = Meter.CreateCounter<long>("redis.pool.reaps");
    public static readonly Counter<long> PoolValidations = Meter.CreateCounter<long>("redis.pool.validations");
    public static readonly Counter<long> PoolValidationFailures = Meter.CreateCounter<long>("redis.pool.validation.failures");

    public static readonly Counter<long> CommandCalls = Meter.CreateCounter<long>("redis.cmd.calls");
    public static readonly Counter<long> CommandFailures = Meter.CreateCounter<long>("redis.cmd.failures");
    public static readonly Histogram<double> CommandMs = Meter.CreateHistogram<double>("redis.cmd.ms");

    public static readonly Counter<long> BytesSent = Meter.CreateCounter<long>("redis.bytes.sent");
    public static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>("redis.bytes.received");
}
