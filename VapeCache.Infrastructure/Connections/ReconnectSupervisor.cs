using System;
using System.Threading;
using System.Threading.Tasks;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Centralizes reconnect pacing to avoid reconnect storms.
/// </summary>
internal sealed class ReconnectSupervisor
{
    private const int MinBackoffMs = 50;
    private const int MaxBackoffMs = 5_000;
    private const int CircuitOpenMs = 10_000;

    private int _consecutiveFaults;
    private long _nextAllowedConnectTicks;
    private long _circuitOpenUntilTicks;

    public void OnConnected()
    {
        Volatile.Write(ref _consecutiveFaults, 0);
        Volatile.Write(ref _nextAllowedConnectTicks, 0);
        Volatile.Write(ref _circuitOpenUntilTicks, 0);
    }

    public void OnFault(Exception _)
    {
        var faults = Math.Min(30, Interlocked.Increment(ref _consecutiveFaults));

        // Exponential backoff with small jitter.
        var backoffMs = MinBackoffMs * (1 << Math.Min(10, faults - 1));
        if (backoffMs > MaxBackoffMs) backoffMs = MaxBackoffMs;
        var jitter = Random.Shared.Next(0, Math.Max(1, backoffMs / 4));
        backoffMs += jitter;

        var now = DateTime.UtcNow.Ticks;
        var delayTicks = TimeSpan.FromMilliseconds(backoffMs).Ticks;
        Interlocked.Exchange(ref _nextAllowedConnectTicks, now + delayTicks);

        // If we're faulting hard, open a short circuit to avoid hammering.
        if (faults >= 8)
        {
            var openTicks = TimeSpan.FromMilliseconds(CircuitOpenMs).Ticks;
            Interlocked.Exchange(ref _circuitOpenUntilTicks, now + openTicks);
        }
    }

    public async ValueTask WaitBeforeConnectAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow.Ticks;
        var circuitUntil = Volatile.Read(ref _circuitOpenUntilTicks);
        if (circuitUntil > now)
        {
            var delay = new TimeSpan(circuitUntil - now);
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return;
        }

        var next = Volatile.Read(ref _nextAllowedConnectTicks);
        if (next <= now) return;

        var wait = new TimeSpan(next - now);
        await Task.Delay(wait, ct).ConfigureAwait(false);
    }
}
