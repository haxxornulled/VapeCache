namespace VapeCache.Extensions.Aspire;

/// <summary>
/// Readiness state for Aspire startup warmup.
/// </summary>
public interface IVapeCacheStartupReadiness
{
    /// <summary>
    /// Gets the s ready.
    /// </summary>
    bool IsReady { get; }
    /// <summary>
    /// Gets the s running.
    /// </summary>
    bool IsRunning { get; }
    /// <summary>
    /// Gets the target connections.
    /// </summary>
    int TargetConnections { get; }
    /// <summary>
    /// Gets the successful connections.
    /// </summary>
    int SuccessfulConnections { get; }
    /// <summary>
    /// Gets the failed connections.
    /// </summary>
    int FailedConnections { get; }
    /// <summary>
    /// Gets the status.
    /// </summary>
    string? Status { get; }
    /// <summary>
    /// Gets the last error.
    /// </summary>
    Exception? LastError { get; }
    /// <summary>
    /// Gets the completed at utc.
    /// </summary>
    DateTimeOffset? CompletedAtUtc { get; }

    /// <summary>
    /// Executes mark warmup disabled.
    /// </summary>
    void MarkWarmupDisabled();
    /// <summary>
    /// Executes mark running.
    /// </summary>
    void MarkRunning(int targetConnections);
    /// <summary>
    /// Executes mark completed.
    /// </summary>
    void MarkCompleted(bool ready, int successfulConnections, int failedConnections, string? status, Exception? lastError);
}

internal sealed class VapeCacheStartupReadinessState : IVapeCacheStartupReadiness
{
    private int _isReady = 1;
    private int _isRunning;
    private int _targetConnections;
    private int _successfulConnections;
    private int _failedConnections;
    private string? _status = "startup-warmup-disabled";
    private Exception? _lastError;
    private long _completedAtUtcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;
    public int TargetConnections => Volatile.Read(ref _targetConnections);
    public int SuccessfulConnections => Volatile.Read(ref _successfulConnections);
    public int FailedConnections => Volatile.Read(ref _failedConnections);
    public string? Status => Volatile.Read(ref _status);
    public Exception? LastError => Volatile.Read(ref _lastError);
    public DateTimeOffset? CompletedAtUtc
    {
        get
        {
            var value = Interlocked.Read(ref _completedAtUtcUnixMs);
            return value == long.MinValue ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public void MarkWarmupDisabled()
    {
        Volatile.Write(ref _isRunning, 0);
        Volatile.Write(ref _isReady, 1);
        Volatile.Write(ref _targetConnections, 0);
        Volatile.Write(ref _successfulConnections, 0);
        Volatile.Write(ref _failedConnections, 0);
        Volatile.Write(ref _status, "startup-warmup-disabled");
        Volatile.Write(ref _lastError, null);
        Interlocked.Exchange(ref _completedAtUtcUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void MarkRunning(int targetConnections)
    {
        Volatile.Write(ref _isRunning, 1);
        Volatile.Write(ref _isReady, 0);
        Volatile.Write(ref _targetConnections, Math.Max(0, targetConnections));
        Volatile.Write(ref _successfulConnections, 0);
        Volatile.Write(ref _failedConnections, 0);
        Volatile.Write(ref _status, "startup-warmup-running");
        Volatile.Write(ref _lastError, null);
        Interlocked.Exchange(ref _completedAtUtcUnixMs, long.MinValue);
    }

    public void MarkCompleted(bool ready, int successfulConnections, int failedConnections, string? status, Exception? lastError)
    {
        Volatile.Write(ref _isRunning, 0);
        Volatile.Write(ref _isReady, ready ? 1 : 0);
        Volatile.Write(ref _successfulConnections, Math.Max(0, successfulConnections));
        Volatile.Write(ref _failedConnections, Math.Max(0, failedConnections));
        Volatile.Write(ref _status, status);
        Volatile.Write(ref _lastError, lastError);
        Interlocked.Exchange(ref _completedAtUtcUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
