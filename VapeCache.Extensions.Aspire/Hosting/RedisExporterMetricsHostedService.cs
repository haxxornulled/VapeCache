using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VapeCache.Extensions.Aspire.Hosting;

internal sealed partial class RedisExporterMetricsHostedService : BackgroundService
{
    public const string HttpClientName = "VapeCache.RedisExporterMetrics";

    private static readonly TimeSpan OptionWakeSlice = TimeSpan.FromMilliseconds(250);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<RedisExporterMetricsOptions> _optionsMonitor;
    private readonly RedisExporterMetricsState _state;
    private readonly ILogger<RedisExporterMetricsHostedService> _logger;
    private readonly IDisposable? _optionsChangeRegistration;

    private int _configurationDirty = 1;
    private bool _lastPollSuccessful;
    private string? _lastInvalidEndpoint;

    public RedisExporterMetricsHostedService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<RedisExporterMetricsOptions> optionsMonitor,
        RedisExporterMetricsState state,
        ILogger<RedisExporterMetricsHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _state = state;
        _logger = logger;

        _optionsChangeRegistration = _optionsMonitor.OnChange((_, _) =>
        {
            Interlocked.Exchange(ref _configurationDirty, 1);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            NormalizeOptions(options);

            var now = DateTimeOffset.UtcNow;
            if (!options.Enabled)
            {
                _state.SetDisabled(now);
                _lastPollSuccessful = false;
                _lastInvalidEndpoint = null;

                await DelayForOptionsChangeAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                _state.SetFailure(now);
                LogInvalidEndpoint(options.Endpoint);
                await DelayForOptionsChangeAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await PollOnceAsync(endpoint, options.RequestTimeout, stoppingToken).ConfigureAwait(false);
            await DelayForOptionsChangeAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _optionsChangeRegistration?.Dispose();
        base.Dispose();
    }

    private static void NormalizeOptions(RedisExporterMetricsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            options.Endpoint = RedisExporterMetricsOptions.DefaultEndpoint;

        if (options.PollInterval < RedisExporterMetricsOptions.MinimumPollInterval)
            options.PollInterval = RedisExporterMetricsOptions.MinimumPollInterval;

        if (options.RequestTimeout < RedisExporterMetricsOptions.MinimumRequestTimeout)
            options.RequestTimeout = RedisExporterMetricsOptions.MinimumRequestTimeout;
    }

    private async Task PollOnceAsync(Uri endpoint, TimeSpan requestTimeout, CancellationToken stoppingToken)
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(requestTimeout);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                MarkPollingFailure(observedAtUtc, $"HTTP {(int)response.StatusCode}");
                return;
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!RedisExporterMetricsParser.TryParse(payload, out var values))
            {
                MarkPollingFailure(observedAtUtc, "payload did not contain redis_exporter metrics");
                return;
            }

            _state.SetSuccess(values, observedAtUtc);
            _lastInvalidEndpoint = null;

            if (!_lastPollSuccessful)
                LogIngestionHealthy(_logger, endpoint);

            _lastPollSuccessful = true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            MarkPollingFailure(observedAtUtc, "request timeout");
        }
        catch (HttpRequestException ex)
        {
            MarkPollingFailure(observedAtUtc, ex.Message);
        }
        catch (Exception ex)
        {
            _state.SetFailure(observedAtUtc);
            _lastPollSuccessful = false;
            LogUnexpectedPollingError(_logger, ex, endpoint);
        }
    }

    private void MarkPollingFailure(DateTimeOffset observedAtUtc, string reason)
    {
        _state.SetFailure(observedAtUtc);

        if (_lastPollSuccessful)
        {
            LogIngestionFailed(_logger, reason);
        }
        else
        {
            LogIngestionStillFailing(_logger, reason);
        }

        _lastPollSuccessful = false;
    }

    private void LogInvalidEndpoint(string? endpoint)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint) ? "<empty>" : endpoint.Trim();
        if (string.Equals(_lastInvalidEndpoint, normalized, StringComparison.Ordinal))
            return;

        _lastInvalidEndpoint = normalized;
        _lastPollSuccessful = false;
        LogInvalidEndpointWarning(
            _logger,
            endpoint ?? "<null>",
            RedisExporterMetricsOptions.ConfigurationSectionName);
    }

    private async Task DelayForOptionsChangeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var remaining = delay;
        while (remaining > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
        {
            var slice = remaining > OptionWakeSlice ? OptionWakeSlice : remaining;
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);

            if (Interlocked.Exchange(ref _configurationDirty, 0) == 1)
                return;

            remaining -= slice;
        }
    }

    [LoggerMessage(
        EventId = 9021,
        Level = LogLevel.Information,
        Message = "Redis exporter telemetry ingestion is healthy at {Endpoint}")]
    private static partial void LogIngestionHealthy(ILogger logger, Uri endpoint);

    [LoggerMessage(
        EventId = 9022,
        Level = LogLevel.Warning,
        Message = "Redis exporter telemetry ingestion failed: {Reason}")]
    private static partial void LogIngestionFailed(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 9023,
        Level = LogLevel.Debug,
        Message = "Redis exporter telemetry ingestion still failing: {Reason}")]
    private static partial void LogIngestionStillFailing(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 9024,
        Level = LogLevel.Warning,
        Message = "Unexpected error while polling redis_exporter at {Endpoint}")]
    private static partial void LogUnexpectedPollingError(ILogger logger, Exception exception, Uri endpoint);

    [LoggerMessage(
        EventId = 9025,
        Level = LogLevel.Warning,
        Message = "Redis exporter telemetry endpoint \"{Endpoint}\" is invalid. Provide an absolute URI in {Section}.")]
    private static partial void LogInvalidEndpointWarning(ILogger logger, string endpoint, string section);
}
