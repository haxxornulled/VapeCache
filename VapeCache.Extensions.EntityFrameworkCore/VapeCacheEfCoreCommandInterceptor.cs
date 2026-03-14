using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// EF Core command interceptor that produces deterministic query cache keys.
/// This interceptor is intentionally non-invasive and does not alter command execution.
/// </summary>
public sealed partial class VapeCacheEfCoreCommandInterceptor : DbCommandInterceptor
{
    private const int MaxFailureMessageLength = 512;
    private readonly IEfCoreQueryCacheKeyBuilder _keyBuilder;
    private readonly IOptionsMonitor<EfCoreSecondLevelCacheOptions> _optionsMonitor;
    private readonly IEfCoreSecondLevelCacheObserver[] _observers;
    private readonly ILogger<VapeCacheEfCoreCommandInterceptor> _logger;

    /// <summary>
    /// Creates a command interceptor instance.
    /// </summary>
    public VapeCacheEfCoreCommandInterceptor(
        IEfCoreQueryCacheKeyBuilder keyBuilder,
        IOptionsMonitor<EfCoreSecondLevelCacheOptions> optionsMonitor,
        IEnumerable<IEfCoreSecondLevelCacheObserver> observers,
        ILogger<VapeCacheEfCoreCommandInterceptor> logger)
    {
        _keyBuilder = ParanoiaThrowGuard.Against.NotNull(keyBuilder);
        _optionsMonitor = ParanoiaThrowGuard.Against.NotNull(optionsMonitor);
        _logger = ParanoiaThrowGuard.Against.NotNull(logger);
        ParanoiaThrowGuard.Against.NotNull(observers);
        _observers = observers as IEfCoreSecondLevelCacheObserver[] ?? observers.ToArray();
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        TryBuildQueryCacheKey(command, eventData, out _);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        TryBuildQueryCacheKey(command, eventData, out _);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        TryEmitQueryExecutionCompleted(command, eventData, succeeded: true, failure: null);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        TryEmitQueryExecutionCompleted(command, eventData, succeeded: true, failure: null);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => TryEmitQueryExecutionCompleted(command, eventData, succeeded: false, eventData.Exception);

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TryEmitQueryExecutionCompleted(command, eventData, succeeded: false, eventData.Exception);
        return Task.CompletedTask;
    }

    private void TryBuildQueryCacheKey(
        DbCommand command,
        CommandEventData eventData,
        out string? cacheKey)
    {
        cacheKey = null;
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        var commandText = command.CommandText;
        if (string.IsNullOrWhiteSpace(commandText))
            return;

        if (!LooksLikeReadOnlyQuery(commandText))
            return;

        var providerName = eventData.Context?.Database.ProviderName ?? "unknown";
        var key = _keyBuilder.BuildQueryCacheKey(providerName, command);
        cacheKey = key;
        var payload = new EfCoreQueryCacheKeyBuiltEvent(
            CommandId: eventData.CommandId,
            ContextInstanceId: eventData.Context?.ContextId.InstanceId ?? Guid.Empty,
            ProviderName: providerName,
            CacheKey: key,
            CommandTextLength: commandText.Length,
            ParameterCount: command.Parameters.Count);

        NotifyQueryCacheKeyBuilt(in payload, options);

        if (options.EnableCommandKeyDiagnostics)
            LogQueryCacheKeyBuilt(_logger, providerName, payload.ParameterCount, key);
    }

    private void TryEmitQueryExecutionCompleted(
        DbCommand command,
        CommandEndEventData eventData,
        bool succeeded,
        Exception? failure)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || !options.EnableObserverCallbacks || _observers.Length == 0)
            return;

        var commandText = command.CommandText;
        if (string.IsNullOrWhiteSpace(commandText) || !LooksLikeReadOnlyQuery(commandText))
            return;

        var providerName = eventData.Context?.Database.ProviderName ?? "unknown";
        var cacheKey = _keyBuilder.BuildQueryCacheKey(providerName, command);

        var failureType = failure?.GetType().FullName;
        var failureMessage = TruncateFailureMessage(failure?.Message);
        var payload = new EfCoreQueryExecutionCompletedEvent(
            CommandId: eventData.CommandId,
            ContextInstanceId: eventData.Context?.ContextId.InstanceId ?? Guid.Empty,
            ProviderName: providerName,
            CacheKey: cacheKey,
            DurationMs: eventData.Duration.TotalMilliseconds,
            Succeeded: succeeded,
            FailureType: failureType,
            FailureMessage: failureMessage);

        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnQueryExecutionCompleted(payload);
            }
            catch (Exception ex)
            {
                LogObserverCallbackFailed(_logger, nameof(IEfCoreSecondLevelCacheObserver.OnQueryExecutionCompleted), ex);
            }
        }
    }

    private void NotifyQueryCacheKeyBuilt(
        in EfCoreQueryCacheKeyBuiltEvent payload,
        EfCoreSecondLevelCacheOptions options)
    {
        if (!options.EnableObserverCallbacks || _observers.Length == 0)
            return;

        for (var i = 0; i < _observers.Length; i++)
        {
            try
            {
                _observers[i].OnQueryCacheKeyBuilt(payload);
            }
            catch (Exception ex)
            {
                LogObserverCallbackFailed(_logger, nameof(IEfCoreSecondLevelCacheObserver.OnQueryCacheKeyBuilt), ex);
            }
        }
    }

    private static string? TruncateFailureMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        if (message.Length <= MaxFailureMessageLength)
            return message;

        return message[..MaxFailureMessageLength];
    }

    private static bool LooksLikeReadOnlyQuery(string commandText)
    {
        var span = commandText.AsSpan().TrimStart();
        return span.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
               span.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
               span.StartsWith("/*", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(
        EventId = 15001,
        Level = LogLevel.Debug,
        Message = "EF query cache key built. Provider={Provider} Parameters={Parameters} Key={CacheKey}")]
    private static partial void LogQueryCacheKeyBuilt(
        ILogger logger,
        string provider,
        int parameters,
        string cacheKey);

    [LoggerMessage(
        EventId = 15004,
        Level = LogLevel.Debug,
        Message = "EF observer callback failed. Callback={Callback}")]
    private static partial void LogObserverCallbackFailed(ILogger logger, string callback, Exception exception);
}
