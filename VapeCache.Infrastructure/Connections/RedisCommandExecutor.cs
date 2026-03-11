using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using VapeCache.Abstractions.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VapeCache.Infrastructure.Connections;

internal sealed partial class RedisCommandExecutor : IRedisCommandExecutor, IRedisMultiplexerDiagnostics
{
    private static readonly ReadOnlyMemory<byte> CrlfMemory = "\r\n"u8.ToArray();
    private static readonly long StopwatchUtcAnchorTimestamp = Stopwatch.GetTimestamp();
    private static readonly long StopwatchUtcAnchorDateTicks = DateTimeOffset.UtcNow.UtcTicks;
    private static readonly long ScaleEventWindowStopwatchTicks = ToStopwatchTicks(TimeSpan.FromMinutes(1));
    private RedisMultiplexedConnection[] _conns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _readConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _writeConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _bulkConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _bulkReadConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _bulkWriteConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _pubSubConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _pubSubReadConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _pubSubWriteConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _blockingConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _blockingReadConns = Array.Empty<RedisMultiplexedConnection>();
    private RedisMultiplexedConnection[] _blockingWriteConns = Array.Empty<RedisMultiplexedConnection>();
    private readonly System.Threading.Lock _connGate = new();
    private int _rr;
    private int _readRr;
    private int _writeRr;
    private int _bulkRr;
    private int _bulkReadRr;
    private int _bulkWriteRr;
    private RuntimeConfig _runtimeConfig = RuntimeConfig.Empty;
    private readonly IRedisConnectionFactory _factory;
    private readonly RedisConnectionOptions _connectionOptions;
    private readonly bool _clusterRedirectsEnabled;
    private readonly int _maxClusterRedirects;
    private bool _autoscaleEnabled;
    private int _minConnections;
    private int _maxConnections;
    private TimeSpan _autoscaleSampleInterval;
    private TimeSpan _scaleUpWindow;
    private TimeSpan _scaleDownWindow;
    private TimeSpan _scaleUpCooldown;
    private TimeSpan _scaleDownCooldown;
    private double _scaleUpInflightUtilization;
    private double _scaleDownInflightUtilization;
    private int _scaleUpQueueDepthThreshold;
    private double _scaleUpTimeoutRatePerSecThreshold;
    private double _scaleUpP99LatencyMsThreshold;
    private double _scaleDownP95LatencyMsThreshold;
    private readonly CancellationTokenSource _autoscaleCts = new();
    private Task? _autoscaleTask;
    private readonly IDisposable? _muxOptionsChangeRegistration;
    private long _highPressureStreakTicks;
    private long _lowPressureStreakTicks;
    private long _lastScaleUpTicks;
    private long _lastScaleDownTicks;
    private long _lastTimeoutSampleTicks;
    private long _lastTimeoutSampleCount;
    private readonly RollingPercentileLatencySampler _autoscaleLatencySampler = new(1024);
    private int _lastHighSignalCount;
    private double _lastAvgInflightUtilization;
    private double _lastAvgQueueDepth;
    private int _lastMaxQueueDepth;
    private double _lastTimeoutRatePerSec;
    private double _lastRollingP95Ms;
    private double _lastRollingP99Ms;
    private int _lastTargetConnections;
    private int _lastUnhealthyConnections;
    private double _lastReconnectFailureRatePerSec;
    private long _lastScaleEventTicks;
    private string? _lastScaleDirection;
    private string? _lastScaleReason;
    private int _scaleEventsInCurrentMinute;
    private int _maxScaleEventsPerMinute;
    private long _scaleEventWindowStartTicks;
    private int _flapToggleCount;
    private int _flapToggleThreshold;
    private long _lastScaleDirectionTicks;
    private string? _lastScaleDirectionForFlap;
    private TimeSpan _autoscaleFreezeDuration;
    private long _autoscaleFrozenUntilTicks;
    private string? _freezeReason;
    private double _reconnectStormFailureRatePerSecThreshold;
    private long _lastFailureSampleTicks;
    private long _lastFailureSampleCount;
    private readonly ILogger<RedisCommandExecutor> _logger;
    private bool _autoscaleAdvisorMode;
    private double _emergencyScaleUpTimeoutRatePerSecThreshold;
    private TimeSpan _scaleDownDrainTimeout;
    private int _configuredBulkLaneConnections;
    private int _configuredPubSubLaneConnections;
    private int _configuredBlockingLaneConnections;
    private bool _autoAdjustBulkLanes;
    private double _bulkLaneTargetRatio;
    private int _bulkLaneConnections;
    private TimeSpan _bulkLaneResponseTimeout;
    private (string Key, int ValueLen)[]? _msetLengthsCache;
    private JsonSetHeaderCacheEntry? _jsonSetHeaderCache;
    private HGetHeaderCacheEntry? _hgetHeaderCache;
    private HSetHeaderCacheEntry? _hsetHeaderCache;
    private static readonly ReadOnlyMemory<byte> AskingCommand = "*1\r\n$6\r\nASKING\r\n"u8.ToArray();

    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptions<RedisMultiplexerOptions> options)
        : this(
            factory,
            options.Value,
            new RedisConnectionOptions(),
            NullLogger<RedisCommandExecutor>.Instance)
    {
    }

    [ActivatorUtilitiesConstructor]
    public RedisCommandExecutor(
        IRedisConnectionFactory factory,
        IOptionsMonitor<RedisMultiplexerOptions> options,
        IOptionsMonitor<RedisConnectionOptions>? connectionOptions = null,
        ILogger<RedisCommandExecutor>? logger = null)
        : this(
            factory,
            options.CurrentValue,
            connectionOptions?.CurrentValue ?? new RedisConnectionOptions(),
            logger ?? NullLogger<RedisCommandExecutor>.Instance)
    {
        _muxOptionsChangeRegistration = options.OnChange((updated, _) =>
        {
            var applied = RedisRuntimeOptionsNormalizer.NormalizeMultiplexer(updated);
            var runtime = BuildRuntimeConfig(applied);
            Volatile.Write(ref _runtimeConfig, runtime);
            LogNormalizationIfChanged("RedisMultiplexer", updated, applied);
            ApplyAutoscaleOptions(runtime.Multiplexer);
        });
    }

    private RedisCommandExecutor(
        IRedisConnectionFactory factory,
        RedisMultiplexerOptions configuredMuxOptions,
        RedisConnectionOptions configuredConnectionOptions,
        ILogger<RedisCommandExecutor> logger)
    {
        _logger = logger;

        var o = RedisRuntimeOptionsNormalizer.NormalizeMultiplexer(configuredMuxOptions);
        var connOpts = RedisRuntimeOptionsNormalizer.NormalizeConnection(configuredConnectionOptions);
        ValidateTlsConfiguration(connOpts);
        _factory = factory;
        _connectionOptions = connOpts;
        _clusterRedirectsEnabled = connOpts.EnableClusterRedirection;
        _maxClusterRedirects = Math.Max(0, connOpts.MaxClusterRedirects);
        var runtime = BuildRuntimeConfig(o);
        _runtimeConfig = runtime;
        _bulkLaneResponseTimeout = runtime.BulkLaneResponseTimeout;
        var count = Math.Max(1, runtime.Multiplexer.Connections);
        _configuredBulkLaneConnections = Math.Max(0, runtime.Multiplexer.BulkLaneConnections);
        _configuredPubSubLaneConnections = Math.Max(0, runtime.Multiplexer.PubSubLaneConnections);
        _configuredBlockingLaneConnections = Math.Max(0, runtime.Multiplexer.BlockingLaneConnections);
        _autoAdjustBulkLanes = runtime.Multiplexer.AutoAdjustBulkLanes;
        _bulkLaneTargetRatio = NormalizeBulkLaneTargetRatio(runtime.Multiplexer.BulkLaneTargetRatio);
        var laneBudget = ResolveLaneBudget(
            _configuredBulkLaneConnections,
            _configuredPubSubLaneConnections,
            _configuredBlockingLaneConnections,
            _autoAdjustBulkLanes,
            _bulkLaneTargetRatio,
            count);
        _bulkLaneConnections = laneBudget.BulkConnections;
        _conns = new RedisMultiplexedConnection[laneBudget.FastConnections];
        for (var i = 0; i < _conns.Length; i++)
            _conns[i] = CreateFastConnection();
        _bulkConns = new RedisMultiplexedConnection[laneBudget.BulkConnections];
        for (var i = 0; i < _bulkConns.Length; i++)
            _bulkConns[i] = CreateBulkConnection();
        _pubSubConns = new RedisMultiplexedConnection[laneBudget.PubSubConnections];
        for (var i = 0; i < _pubSubConns.Length; i++)
            _pubSubConns[i] = CreatePubSubConnection();
        _blockingConns = new RedisMultiplexedConnection[laneBudget.BlockingConnections];
        for (var i = 0; i < _blockingConns.Length; i++)
            _blockingConns[i] = CreateBlockingConnection();
        RebuildLanesUnsafe();
        LogNormalizationIfChanged("RedisMultiplexer", configuredMuxOptions, o);
        LogNormalizationIfChanged("RedisConnection", configuredConnectionOptions, connOpts);
        ApplyAutoscaleOptions(runtime.Multiplexer);
        _autoscaleTask = Task.Run(AutoscaleLoopAsync);
    }

    private static RedisTransportProfile NormalizeTransportProfile(RedisTransportProfile profile)
        => Enum.IsDefined(profile)
            ? profile
            : RedisTransportProfile.FullTilt;

    private static double NormalizeBulkLaneTargetRatio(double configuredRatio)
    {
        if (double.IsNaN(configuredRatio) || double.IsInfinity(configuredRatio))
            return 0.25d;

        return Math.Clamp(configuredRatio, 0d, 0.90d);
    }

    private static int ResolveBulkLaneConnections(
        int configuredBulkLaneConnections,
        bool autoAdjustBulkLanes,
        double bulkLaneTargetRatio,
        int totalConnectionBudget)
    {
        var normalizedTotalConnections = Math.Max(1, totalConnectionBudget);
        if (normalizedTotalConnections <= 1)
            return 0;

        var maxBulkLanes = normalizedTotalConnections - 1;

        if (autoAdjustBulkLanes)
        {
            var normalizedRatio = NormalizeBulkLaneTargetRatio(bulkLaneTargetRatio);
            if (normalizedRatio <= 0d)
                return 0;

            var targetByRatio = (int)Math.Round(
                normalizedTotalConnections * normalizedRatio,
                MidpointRounding.AwayFromZero);
            if (targetByRatio <= 0)
                targetByRatio = 1;
            return Math.Clamp(targetByRatio, 1, maxBulkLanes);
        }

        var requested = Math.Max(0, configuredBulkLaneConnections);
        return Math.Min(requested, maxBulkLanes);
    }

    private static (int PubSubConnections, int BlockingConnections, int ScalableConnections) ResolveReservedLaneConnections(
        int configuredPubSubLaneConnections,
        int configuredBlockingLaneConnections,
        int totalConnectionBudget)
    {
        var normalizedTotalConnections = Math.Max(1, totalConnectionBudget);
        var reservedBudget = Math.Max(0, normalizedTotalConnections - 1);
        var pubSubConnections = Math.Clamp(Math.Max(0, configuredPubSubLaneConnections), 0, reservedBudget);
        var remainingReservedBudget = Math.Max(0, reservedBudget - pubSubConnections);
        var blockingConnections = Math.Clamp(Math.Max(0, configuredBlockingLaneConnections), 0, remainingReservedBudget);
        var scalableConnections = Math.Max(1, normalizedTotalConnections - pubSubConnections - blockingConnections);
        return (pubSubConnections, blockingConnections, scalableConnections);
    }

    private static (int FastConnections, int BulkConnections, int PubSubConnections, int BlockingConnections) ResolveLaneBudget(
        int configuredBulkLaneConnections,
        int configuredPubSubLaneConnections,
        int configuredBlockingLaneConnections,
        bool autoAdjustBulkLanes,
        double bulkLaneTargetRatio,
        int totalConnectionBudget)
    {
        var reserved = ResolveReservedLaneConnections(
            configuredPubSubLaneConnections,
            configuredBlockingLaneConnections,
            totalConnectionBudget);
        var bulkConnections = ResolveBulkLaneConnections(
            configuredBulkLaneConnections,
            autoAdjustBulkLanes,
            bulkLaneTargetRatio,
            reserved.ScalableConnections);
        var fastConnections = Math.Max(1, reserved.ScalableConnections - bulkConnections);
        return (fastConnections, bulkConnections, reserved.PubSubConnections, reserved.BlockingConnections);
    }

    private static TimeSpan ResolveBulkLaneResponseTimeout(RedisMultiplexerOptions options)
    {
        if (options.BulkLaneResponseTimeout > TimeSpan.Zero)
            return options.BulkLaneResponseTimeout;

        return TimeSpan.FromSeconds(5);
    }

    private static RuntimeConfig BuildRuntimeConfig(RedisMultiplexerOptions options)
        => new(
            options,
            options.EnableCommandInstrumentation,
            ResolveBulkLaneResponseTimeout(options));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetTotalConnectionCountUnsafe()
        => _conns.Length + _bulkConns.Length + _pubSubConns.Length + _blockingConns.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RuntimeConfig ReadRuntimeConfig()
        => Volatile.Read(ref _runtimeConfig);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsCommandInstrumentationEnabled()
        => ReadRuntimeConfig().EnableCommandInstrumentation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ToStopwatchTicks(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return 0;

        var ticks = duration.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1L, (long)Math.Ceiling(ticks));
    }

    private static DateTimeOffset ToUtcTimestamp(long stopwatchTicks)
    {
        var deltaStopwatchTicks = stopwatchTicks - StopwatchUtcAnchorTimestamp;
        var deltaUtcTicks = (long)(deltaStopwatchTicks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        return new DateTimeOffset(StopwatchUtcAnchorDateTicks + deltaUtcTicks, TimeSpan.Zero);
    }

    private void LogNormalizationIfChanged(
        string optionsName,
        RedisMultiplexerOptions configured,
        RedisMultiplexerOptions effective)
    {
        if (configured == effective)
            return;

        // Transport profile application is intentional and not a guardrail correction.
        var profiled = RedisTransportProfiles.Apply(
            configured with { TransportProfile = NormalizeTransportProfile(configured.TransportProfile) });
        if (profiled == effective)
            return;

        LogOptionsNormalized(_logger, optionsName);
    }

    private void LogNormalizationIfChanged(
        string optionsName,
        RedisConnectionOptions configured,
        RedisConnectionOptions effective)
    {
        if (configured == effective)
            return;

        // Transport profile application is intentional and not a guardrail correction.
        var profiled = RedisTransportProfiles.Apply(
            configured with { TransportProfile = NormalizeTransportProfile(configured.TransportProfile) });
        if (profiled == effective)
            return;

        LogOptionsNormalized(_logger, optionsName);
    }

    /// <summary>
    /// Creates value.
    /// </summary>
    public IRedisBatch CreateBatch()
        => new RedisBatch(this);

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            throw new ArgumentOutOfRangeException(nameof(value), "NaN is not a valid Redis score.");
        if (double.IsPositiveInfinity(value))
            return "+inf";
        if (double.IsNegativeInfinity(value))
            return "-inf";
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static int GetBulkLength(RedisRespReader.RespValue value)
    {
        if (value.Bulk is null)
            return 0;
        if (value.BulkLength > 0)
            return value.BulkLength;
        return value.Bulk.Length;
    }

    private static long ParseCursor(RedisRespReader.RespValue value)
    {
        if (value.Kind is not RedisRespReader.RespKind.BulkString)
            throw new InvalidOperationException($"Unexpected SCAN cursor kind: {value.Kind}");

        var length = GetBulkLength(value);
        var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
        if (TryParseInt64Utf8(span, out var cursor))
            return cursor;

        throw new InvalidOperationException("Invalid SCAN cursor value.");
    }

    private static double ParseDouble(RedisRespReader.RespValue value)
    {
        if (value.Kind == RedisRespReader.RespKind.SimpleString)
        {
            var text = value.Text ?? throw new InvalidOperationException("Unexpected score value: empty simple string.");
            if (TryParseDoubleText(text, out var score))
                return score;

            throw new InvalidOperationException($"Invalid score value: {text}");
        }

        if (value.Kind == RedisRespReader.RespKind.BulkString)
        {
            var length = GetBulkLength(value);
            var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
            if (TryParseDoubleUtf8(span, out var parsed))
                return parsed;

            throw new InvalidOperationException("Invalid score value.");
        }

        throw new InvalidOperationException($"Unexpected score kind: {value.Kind}");
    }

    private static long ParseLong(RedisRespReader.RespValue value)
    {
        if (value.Kind == RedisRespReader.RespKind.Integer)
            return value.IntegerValue;
        if (value.Kind != RedisRespReader.RespKind.BulkString)
            throw new InvalidOperationException($"Unexpected integer kind: {value.Kind}");

        var length = GetBulkLength(value);
        var span = (value.Bulk ?? Array.Empty<byte>()).AsSpan(0, length);
        if (TryParseInt64Utf8(span, out var parsed))
            return parsed;

        throw new InvalidOperationException("Invalid integer value.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseInt64Utf8(ReadOnlySpan<byte> value, out long parsed)
        => Utf8Parser.TryParse(value, out parsed, out var consumed) && consumed == value.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseDoubleUtf8(ReadOnlySpan<byte> value, out double parsed)
    {
        if (Utf8Parser.TryParse(value, out parsed, out var consumed) && consumed == value.Length)
            return true;

        return TryParseRedisSpecialDoubleUtf8(value, out parsed);
    }

    private static bool TryParseDoubleText(string value, out double parsed)
    {
        if (string.Equals(value, "+inf", StringComparison.OrdinalIgnoreCase))
        {
            parsed = double.PositiveInfinity;
            return true;
        }

        if (string.Equals(value, "-inf", StringComparison.OrdinalIgnoreCase))
        {
            parsed = double.NegativeInfinity;
            return true;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseRedisSpecialDoubleUtf8(ReadOnlySpan<byte> value, out double parsed)
    {
        parsed = default;

        if (value.Length == 4 &&
            value[0] == (byte)'+' &&
            IsAsciiI(value[1]) &&
            IsAsciiN(value[2]) &&
            IsAsciiF(value[3]))
        {
            parsed = double.PositiveInfinity;
            return true;
        }

        if (value.Length == 4 &&
            value[0] == (byte)'-' &&
            IsAsciiI(value[1]) &&
            IsAsciiN(value[2]) &&
            IsAsciiF(value[3]))
        {
            parsed = double.NegativeInfinity;
            return true;
        }

        if (value.Length == 3 &&
            IsAsciiN(value[0]) &&
            IsAsciiA(value[1]) &&
            IsAsciiN(value[2]))
        {
            parsed = double.NaN;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiI(byte value) => (value | 0x20) == (byte)'i';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiN(byte value) => (value | 0x20) == (byte)'n';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiF(byte value) => (value | 0x20) == (byte)'f';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAsciiA(byte value) => (value | 0x20) == (byte)'a';

    private static bool IsRedisIndexAlreadyExists(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current.Message.Contains("index already exists", StringComparison.OrdinalIgnoreCase))
                return true;

            if (current.InnerException is null)
                break;
        }

        return false;
    }

    private readonly record struct RedisClusterRedirectTarget(bool IsAsk, int Slot, string Host, int Port)
    {
        public string Kind => IsAsk ? "ASK" : "MOVED";
    }

    private static bool TryParseClusterRedirect(Exception ex, out RedisClusterRedirectTarget redirect)
        => TryParseClusterRedirectMessage(ex.Message, out redirect);

    private static bool TryParseClusterRedirectMessage(string? message, out RedisClusterRedirectTarget redirect)
    {
        redirect = default;
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.AsSpan().Trim();
        const string prefix = "Redis error:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..].TrimStart();

        if (!TryReadToken(ref text, out var redirectToken))
            return false;

        var isAsk = false;
        if (redirectToken.Equals("ASK", StringComparison.OrdinalIgnoreCase))
        {
            isAsk = true;
        }
        else if (!redirectToken.Equals("MOVED", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadToken(ref text, out var slotToken) ||
            !int.TryParse(slotToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
            return false;

        if (!TryReadToken(ref text, out var endpointToken) ||
            !TryParseHostPort(endpointToken, out var host, out var port))
            return false;

        redirect = new RedisClusterRedirectTarget(isAsk, slot, host, port);
        return true;
    }

    private static bool TryParseHostPort(ReadOnlySpan<char> endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        endpoint = endpoint.Trim();
        if (endpoint.IsEmpty)
            return false;

        if (endpoint[0] == '[')
        {
            var closeBracket = endpoint.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 2 >= endpoint.Length || endpoint[closeBracket + 1] != ':')
                return false;

            var hostSpan = endpoint[1..closeBracket];
            if (!TryParsePort(endpoint[(closeBracket + 2)..], out port))
                return false;

            host = hostSpan.ToString();
            return host.Length != 0;
        }

        var lastColon = endpoint.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == endpoint.Length - 1)
            return false;

        var hostCandidate = endpoint[..lastColon];
        if (!TryParsePort(endpoint[(lastColon + 1)..], out port))
            return false;

        host = hostCandidate.ToString();
        return host.Length != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParsePort(ReadOnlySpan<char> value, out int port)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
            && port is >= 1 and <= 65535;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadToken(ref ReadOnlySpan<char> input, out ReadOnlySpan<char> token)
    {
        input = input.TrimStart();
        if (input.IsEmpty)
        {
            token = default;
            return false;
        }

        var splitIndex = IndexOfWhitespace(input);
        if (splitIndex < 0)
        {
            token = input;
            input = ReadOnlySpan<char>.Empty;
            return true;
        }

        token = input[..splitIndex];
        input = input[(splitIndex + 1)..];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOfWhitespace(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }

    private async ValueTask<RedisRespReader.RespValue> ExecuteWithClusterRedirectsAsync(
        Func<CancellationToken, ValueTask<RedisRespReader.RespValue>> primary,
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        CancellationToken ct)
    {
        try
        {
            return await primary(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (_clusterRedirectsEnabled && _maxClusterRedirects > 0 && TryParseClusterRedirect(ex, out var redirect))
        {
            return await ExecuteClusterRedirectLoopAsync(command, poolBulk, redirect, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<RedisRespReader.RespValue> ExecuteClusterRedirectLoopAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        RedisClusterRedirectTarget redirect,
        CancellationToken ct)
    {
        var current = redirect;
        for (var attempt = 1; attempt <= _maxClusterRedirects; attempt++)
        {
            var response = await ExecuteCommandAgainstRedirectTargetAsync(command, poolBulk, current, ct).ConfigureAwait(false);
            if (response.Kind != RedisRespReader.RespKind.Error)
                return response;

            if (!TryParseClusterRedirectMessage(response.Text, out var next))
                return response;

            current = next;
        }

        throw new InvalidOperationException(
            $"Redis cluster redirect limit exceeded after {_maxClusterRedirects} hops. Last target={current.Host}:{current.Port} slot={current.Slot}.");
    }

    private async ValueTask<RedisRespReader.RespValue> ExecuteCommandAgainstRedirectTargetAsync(
        ReadOnlyMemory<byte> command,
        bool poolBulk,
        RedisClusterRedirectTarget redirect,
        CancellationToken ct)
    {
        var connectTimeout = _connectionOptions.ConnectTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : _connectionOptions.ConnectTimeout;

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(connectTimeout);
        var token = connectCts.Token;

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        TryConfigureSocket(socket);
        await socket.ConnectAsync(redirect.Host, redirect.Port, token).ConfigureAwait(false);

        await using Stream stream = new NetworkStream(socket, ownsSocket: false);
        var activeStream = stream;

        if (_connectionOptions.UseTls)
        {
            var ssl = new SslStream(
                stream,
                leaveInnerStreamOpen: false,
                GetServerCertificateValidationCallback(_connectionOptions));

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _connectionOptions.TlsHost ?? redirect.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, token).ConfigureAwait(false);

            activeStream = ssl;
        }

        await SendHelloAuthAndAskingIfNeededAsync(activeStream, redirect.IsAsk, token).ConfigureAwait(false);

        await activeStream.WriteAsync(command, token).ConfigureAwait(false);
        await activeStream.FlushAsync(token).ConfigureAwait(false);

        return await RedisRespReader.ReadAsync(
            activeStream,
            token,
            maxBulkStringBytes: _connectionOptions.MaxBulkStringBytes,
            maxArrayDepth: _connectionOptions.MaxArrayDepth).ConfigureAwait(false);
    }

    private static RemoteCertificateValidationCallback? GetServerCertificateValidationCallback(RedisConnectionOptions options)
    {
        if (!options.AllowInvalidCert)
            return null;

        if (options.UseTls && IsProductionEnvironment())
        {
            throw new InvalidOperationException(
                "AllowInvalidCert=true is not permitted in production environments. " +
                "This setting bypasses TLS certificate validation and creates a critical security vulnerability. " +
                "Use proper CA-signed certificates or set ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT to Development.");
        }

        return static (_, _, _, errors) => errors == SslPolicyErrors.None || !IsProductionEnvironment();
    }

    private static void ValidateTlsConfiguration(RedisConnectionOptions options)
    {
        _ = GetServerCertificateValidationCallback(options);
    }

    private static bool IsProductionEnvironment()
    {
        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotnetEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var env = aspNetEnv ?? dotnetEnv ?? "Production";
        return !string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(env, "Staging", StringComparison.OrdinalIgnoreCase);
    }

    private void TryConfigureSocket(Socket socket)
    {
        try { socket.NoDelay = _connectionOptions.EnableTcpNoDelay; } catch { }
        try
        {
            if (_connectionOptions.TcpSendBufferBytes > 0)
                socket.SendBufferSize = Math.Clamp(_connectionOptions.TcpSendBufferBytes, 4 * 1024, 4 * 1024 * 1024);
        }
        catch { }
        try
        {
            if (_connectionOptions.TcpReceiveBufferBytes > 0)
                socket.ReceiveBufferSize = Math.Clamp(_connectionOptions.TcpReceiveBufferBytes, 4 * 1024, 4 * 1024 * 1024);
        }
        catch { }
    }

    private async ValueTask SendHelloAuthAndAskingIfNeededAsync(Stream stream, bool askRedirect, CancellationToken ct)
    {
        var protocolVersion = _connectionOptions.RespProtocolVersion is 2 or 3
            ? _connectionOptions.RespProtocolVersion
            : 2;

        var helloLen = RedisRespProtocol.GetHelloCommandLength(protocolVersion);
        var helloBuffer = ArrayPool<byte>.Shared.Rent(helloLen);
        try
        {
            var written = RedisRespProtocol.WriteHelloCommand(helloBuffer.AsSpan(0, helloLen), protocolVersion);
            await stream.WriteAsync(helloBuffer.AsMemory(0, written), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(helloBuffer);
        }

        await RedisRespProtocol.SkipHelloResponseAsync(stream, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_connectionOptions.Password))
        {
            var authLen = RedisRespProtocol.GetAuthCommandLength(_connectionOptions.Username, _connectionOptions.Password);
            var authBuffer = ArrayPool<byte>.Shared.Rent(authLen);
            try
            {
                var written = RedisRespProtocol.WriteAuthCommand(authBuffer.AsSpan(0, authLen), _connectionOptions.Username, _connectionOptions.Password);
                await stream.WriteAsync(authBuffer.AsMemory(0, written), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(authBuffer);
            }

            await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
        }

        if (askRedirect)
        {
            await stream.WriteAsync(AskingCommand, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TryGetAsync(key, ct, out var fastTask))
            return fastTask;

        return GetAsyncSlow(key, ct);
    }

    private async ValueTask<byte[]?> GetAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GET");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            var command = rented.AsMemory(0, written);
            var redirectCommand = _clusterRedirectsEnabled
                ? command.ToArray()
                : null;

            var resp = await ExecuteWithClusterRedirectsAsync(
                token => conn.ExecuteAsync(
                    command,
                    payload: ReadOnlyMemory<byte>.Empty,
                    appendCrlf: false,
                    poolBulk: false,
                    token,
                    headerBuffer: rented),
                redirectCommand ?? command,
                poolBulk: false,
                ct).ConfigureAwait(false);
            rented = null; // returned by writer
            switch (resp.Kind)
            {
                case RedisRespReader.RespKind.NullBulkString:
                    return null;
                case RedisRespReader.RespKind.BulkString:
                    return resp.BulkIsPooled
                        ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                        : resp.Bulk;
                default:
                {
                    var ex = new InvalidOperationException($"Unexpected GET response: {resp.Kind}");
                    RedisRespReader.ReturnBuffers(resp);
                    await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                    throw ex;
                }
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(conn, respTask, "GET");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<RedisValueLease> GetLeaseAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TryGetLeaseAsync(key, ct, out var fastTask))
            return fastTask;

        return GetLeaseAsyncSlow(key, ct);
    }

    private async ValueTask<RedisValueLease> GetLeaseAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GET");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            var command = rented.AsMemory(0, written);
            var redirectCommand = _clusterRedirectsEnabled
                ? command.ToArray()
                : null;

            var resp = await ExecuteWithClusterRedirectsAsync(
                token => conn.ExecuteAsync(
                    command,
                    payload: ReadOnlyMemory<byte>.Empty,
                    appendCrlf: false,
                    poolBulk: true,
                    token,
                    headerBuffer: rented),
                redirectCommand ?? command,
                poolBulk: true,
                ct).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalLeaseResponseAsync(conn, "GET", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetGetCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(conn, respTask, "GET");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<byte[]?> GetExAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled())
        {
            int? ttlMs = null;
            if (ttl is not null)
            {
                var ms = (long)ttl.Value.TotalMilliseconds;
                ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
            }

            if (TryQueueGetExFast(key, ttlMs, ct, out var fastTask))
                return fastTask;
        }

        return GetExAsyncSlow(key, ttl, ct);
    }

    private async ValueTask<byte[]?> GetExAsyncSlow(string key, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETEX");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalBytesResponseAsync(conn, "GETEX", resp, copyPooled: true).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetExAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        RecordCommandCall();
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(conn, respTask, "GETEX");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public ValueTask<RedisValueLease> GetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryGetExLeaseAsync(key, ttl, ct, out var fastTask))
            return fastTask;

        return GetExLeaseAsyncSlow(key, ttl, ct);
    }

    private async ValueTask<RedisValueLease> GetExLeaseAsyncSlow(string key, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETEX");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            conn = NextBulk();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalLeaseResponseAsync(conn, "GETEX", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public async ValueTask<byte[]?> GetRangeAsync(string key, long start, long end, CancellationToken ct)
    {
        using var activity = StartCommandActivity("GETRANGE");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetGetRangeCommandLength(key, start, end);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetRangeCommand(rented.AsSpan(0, len), key, start, end);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalBytesResponseAsync(conn, "GETRANGE", resp, copyPooled: true).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> MGetAsync(string[] keys, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MGET");
        activity?.SetTag("db.redis.key_count", keys.Length);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetMGetCommandLength(keys);
        if (len == 0) return Array.Empty<byte[]?>();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteMGetCommand(rented.AsSpan(0, len), keys);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return new byte[]?[keys.Length];

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "MGET", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.NullBulkString)
                    {
                        result[i] = null;
                        continue;
                    }

                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                    {
                        result[i] = items[i].Bulk;
                        continue;
                    }

                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "MGET", resp, returnBuffers: false).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Sets value.
    /// </summary>
    public ValueTask<bool> SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TrySetAsync(key, value, ttl, ct, out var fastTask))
            return fastTask;

        return SetAsyncSlow(key, value, ttl, ct);
    }

    private async ValueTask<bool> SetAsyncSlow(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SET");
        activity?.SetTag("db.redis.ttl_ms", ttl is null ? null : (long)ttl.Value.TotalMilliseconds);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();

            // For SET without TTL, use scatter/gather I/O for zero-copy performance
            if (ttlMs is null)
            {
                var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, null);
                var headerLen = len - value.Length - 2; // exclude value bytes + CRLF
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteSetCommandHeader(rented.AsSpan(0, headerLen), key, value.Length, null);

                byte[]? redirectCommand = null;
                if (_clusterRedirectsEnabled)
                {
                    var fullLen = RedisRespProtocol.GetSetCommandLength(key, value.Length, null);
                    redirectCommand = GC.AllocateUninitializedArray<byte>(fullLen);
                    RedisRespProtocol.WriteSetCommand(redirectCommand.AsSpan(0, fullLen), key, value.Span, null);
                }

                var resp = await ExecuteWithClusterRedirectsAsync(
                    token => conn.ExecuteAsync(
                        rented.AsMemory(0, written),
                        payload: value,
                        appendCrlf: true,
                        poolBulk: false,
                        token,
                        headerBuffer: rented),
                    redirectCommand ?? rented.AsMemory(0, written),
                    poolBulk: false,
                    ct).ConfigureAwait(false);
                rented = null; // returned by writer

                try
                {
                    if (resp.Kind == RedisRespReader.RespKind.Error)
                        throw new InvalidOperationException($"Redis error: {resp.Text}");

                    if (resp.Kind != RedisRespReader.RespKind.SimpleString ||
                        !(ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK"))
                    {
                        var ex = new InvalidOperationException(FormatUnexpectedSetResponse(resp));
                        await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                        throw ex;
                    }

                    return true;
                }
                finally
                {
                    RedisRespReader.ReturnBuffers(resp);
                }
            }
            else
            {
                // Use PSETEX so TTL writes can stay on the zero-copy header+payload path.
                var len = RedisRespProtocol.GetPSetExCommandLength(key, value.Length, ttlMs.Value);
                var headerLen = len - value.Length - 2; // exclude value bytes + CRLF
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WritePSetExCommandHeader(rented.AsSpan(0, headerLen), key, ttlMs.Value, value.Length);
                var command = rented.AsMemory(0, written);
                byte[]? redirectCommand = null;
                if (_clusterRedirectsEnabled)
                {
                    var fullLen = RedisRespProtocol.GetPSetExCommandLength(key, value.Length, ttlMs.Value);
                    redirectCommand = GC.AllocateUninitializedArray<byte>(fullLen);
                    RedisRespProtocol.WritePSetExCommand(redirectCommand.AsSpan(0, fullLen), key, value.Span, ttlMs.Value);
                }

                var resp = await ExecuteWithClusterRedirectsAsync(
                    token => conn.ExecuteAsync(
                        command,
                        payload: value,
                        appendCrlf: true,
                        poolBulk: false,
                        token,
                        headerBuffer: rented),
                    redirectCommand ?? command,
                    poolBulk: false,
                    ct).ConfigureAwait(false);
                rented = null; // returned by writer

                try
                {
                    if (resp.Kind == RedisRespReader.RespKind.Error)
                        throw new InvalidOperationException($"Redis error: {resp.Text}");

                    if (resp.Kind != RedisRespReader.RespKind.SimpleString ||
                        !(ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK"))
                    {
                        var ex = new InvalidOperationException(FormatUnexpectedSetResponse(resp));
                        await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                        throw ex;
                    }

                    return true;
                }
                finally
                {
                    RedisRespReader.ReturnBuffers(resp);
                }
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null)
            {
                if (ttlMs is not null)
                    ArrayPool<byte>.Shared.Return(rented);
                else if (conn is not null)
                    conn.ReturnHeaderBuffer(rented);
            }
        }
    }

    private static string FormatUnexpectedResponse(string operation, RedisRespReader.RespValue response)
    {
        var prefix = $"Unexpected {operation} response:";
        return response.Kind switch
        {
            RedisRespReader.RespKind.SimpleString => $"{prefix} SimpleString '{response.Text ?? string.Empty}'.",
            RedisRespReader.RespKind.BulkString => $"{prefix} BulkString len={GetBulkLength(response)}.",
            RedisRespReader.RespKind.Integer => $"{prefix} Integer {response.IntegerValue}.",
            RedisRespReader.RespKind.NullBulkString => $"{prefix} NullBulkString.",
            RedisRespReader.RespKind.Array => $"{prefix} Array len={response.ArrayLength}.",
            RedisRespReader.RespKind.NullArray => $"{prefix} NullArray.",
            RedisRespReader.RespKind.Push => $"{prefix} Push len={response.ArrayLength}.",
            _ => $"{prefix} {response.Kind}."
        };
    }

    private static string FormatUnexpectedSetResponse(RedisRespReader.RespValue response)
        => FormatUnexpectedResponse("SET", response);

    private static async ValueTask<T> ThrowUnexpectedResponseAndResetAsync<T>(
        RedisMultiplexedConnection conn,
        string operation,
        RedisRespReader.RespValue response,
        bool returnBuffers = true)
    {
        var ex = new InvalidOperationException(FormatUnexpectedResponse(operation, response));
        if (returnBuffers)
            RedisRespReader.ReturnBuffers(response);
        await conn.ResetTransportAsync(ex).ConfigureAwait(false);
        throw ex;
    }

    private static async ValueTask<byte[]?> ReadOptionalBytesResponseAsync(
        RedisMultiplexedConnection conn,
        string operation,
        RedisRespReader.RespValue resp,
        bool copyPooled)
    {
        if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
            return null;

        if (resp.Kind == RedisRespReader.RespKind.BulkString)
        {
            if (copyPooled && resp.BulkIsPooled && resp.Bulk is not null)
                return resp.Bulk.AsSpan(0, resp.BulkLength).ToArray();
            return resp.Bulk;
        }

        return await ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, operation, resp).ConfigureAwait(false);
    }

    private static async ValueTask<RedisValueLease> ReadOptionalLeaseResponseAsync(
        RedisMultiplexedConnection conn,
        string operation,
        RedisRespReader.RespValue resp)
    {
        if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
            return RedisValueLease.Null;

        if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
            return RedisValueLease.Create(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

        return await ThrowUnexpectedResponseAndResetAsync<RedisValueLease>(conn, operation, resp).ConfigureAwait(false);
    }

    private static async ValueTask<long> ReadIntegerResponseAsync(
        RedisMultiplexedConnection conn,
        string operation,
        RedisRespReader.RespValue resp)
    {
        if (resp.Kind == RedisRespReader.RespKind.Integer)
            return resp.IntegerValue;

        return await ThrowUnexpectedResponseAndResetAsync<long>(conn, operation, resp).ConfigureAwait(false);
    }

    private static async ValueTask<string> ReadSimpleStringResponseAsync(
        RedisMultiplexedConnection conn,
        string operation,
        RedisRespReader.RespValue resp)
    {
        if (resp.Kind == RedisRespReader.RespKind.SimpleString)
            return resp.Text ?? string.Empty;

        return await ThrowUnexpectedResponseAndResetAsync<string>(conn, operation, resp).ConfigureAwait(false);
    }

    private static async ValueTask<bool> ReadSetResponseAsync(
        RedisMultiplexedConnection conn,
        RedisRespReader.RespValue resp)
    {
        if (resp.Kind == RedisRespReader.RespKind.Error)
            throw new InvalidOperationException($"Redis error: {resp.Text}");

        if (resp.Kind == RedisRespReader.RespKind.SimpleString &&
            (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK"))
        {
            return true;
        }

        return await ThrowUnexpectedResponseAndResetAsync<bool>(conn, "SET", resp).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> MSetAsync((string Key, ReadOnlyMemory<byte> Value)[] items, CancellationToken ct)
    {
        using var activity = StartCommandActivity("MSET");
        activity?.SetTag("db.redis.key_count", items.Length);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        if (items.Length == 0) return true;

        var len = RedisRespProtocol.GetMSetCommandLength(items);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = RedisRespProtocol.WriteMSetCommand(rented.AsSpan(0, len), items);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                poolBulk: false,
                ct).ConfigureAwait(false);
            return resp.Kind == RedisRespReader.RespKind.SimpleString && string.Equals(resp.Text, "OK", StringComparison.Ordinal);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<bool> DeleteAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TryDeleteAsync(key, ct, out var fastTask))
            return fastTask;

        return DeleteAsyncSlow(key, ct);
    }

    private async ValueTask<bool> DeleteAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("DEL");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetDelCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteDelCommand(rented.AsSpan(0, len), key);
            var command = rented.AsMemory(0, written);
            var redirectCommand = _clusterRedirectsEnabled
                ? command.ToArray()
                : null;

            var resp = await ExecuteWithClusterRedirectsAsync(
                token => conn.ExecuteAsync(
                    command,
                    payload: ReadOnlyMemory<byte>.Empty,
                    appendCrlf: false,
                    poolBulk: false,
                    token,
                    headerBuffer: rented),
                redirectCommand ?? command,
                poolBulk: false,
                ct).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue > 0;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TryDeleteAsync(string key, CancellationToken ct, out ValueTask<bool> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetDelCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteDelCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapDeleteResponseAsync(conn, respTask);
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> UnlinkAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("UNLINK");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetUnlinkCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteUnlinkCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadIntegerResponseAsync(conn, "HSET", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> TtlSecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TTL");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetTtlCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTtlCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> PTtlMillisecondsAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("PTTL");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetPTtlCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WritePTtlCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : -3;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> HSetAsync(string key, string field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HSET");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            var cached = Volatile.Read(ref _hsetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetHSetCachedHeader(cached, key, field, value.Length, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetHSetCommandLength(key, field, value.Length);
                var headerLen = len - value.Length - 2;
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteHSetCommandHeader(rented.AsSpan(0, headerLen), key, field, value.Length);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _hsetHeaderCache,
                        new HSetHeaderCacheEntry(key, field, value.Length, headerCopy),
                        null);
                }
            }

            var resp = await conn.ExecuteAsync(
                header,
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: headerBuffer).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadIntegerResponseAsync(conn, "LPUSH", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisValueLease> HGetLeaseAsync(string key, string field, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryQueueHGetLeaseFast(key, field, ct, out var fastTask))
            return fastTask;

        return HGetLeaseAsyncSlow(key, field, ct);
    }

    private async ValueTask<RedisValueLease> HGetLeaseAsyncSlow(string key, string field, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HGET");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkRead();
            var cached = Volatile.Read(ref _hgetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetHGetCachedHeader(cached, key, field, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetHGetCommandLength(key, field);
                rented = conn.RentHeaderBuffer(len);
                var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _hgetHeaderCache,
                        new HGetHeaderCacheEntry(key, field, headerCopy),
                        null);
                }
            }

            var resp = await conn.ExecuteAsync(
                header,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: headerBuffer).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalLeaseResponseAsync(conn, "HGET", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]?> HGetAsync(string key, string field, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryHGetAsync(key, field, ct, out var fastTask))
            return fastTask;

        return HGetAsyncSlow(key, field, ct);
    }

    private async ValueTask<byte[]?> HGetAsyncSlow(string key, string field, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HGET");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            var cached = Volatile.Read(ref _hgetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetHGetCachedHeader(cached, key, field, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetHGetCommandLength(key, field);
                rented = conn.RentHeaderBuffer(len);
                var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _hgetHeaderCache,
                        new HGetHeaderCacheEntry(key, field, headerCopy),
                        null);
                }
            }

            var resp = await conn.ExecuteAsync(
                header,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: headerBuffer).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalBytesResponseAsync(conn, "HGET", resp, copyPooled: false).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> HMGetAsync(string key, string[] fields, CancellationToken ct)
    {
        using var activity = StartCommandActivity("HMGET");
        activity?.SetTag("db.redis.field_count", fields.Length);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetHMGetCommandLength(key, fields);
        if (len == 0) return Array.Empty<byte[]?>();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteHMGetCommand(rented.AsSpan(0, len), key, fields);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return new byte[]?[fields.Length];

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "HMGET", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.NullBulkString)
                    {
                        result[i] = null;
                        continue;
                    }

                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                    {
                        result[i] = items[i].Bulk;
                        continue;
                    }

                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "HMGET", resp, returnBuffers: false).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> LPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPUSH");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetLPushCommandLength(key, value.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var headerLen = len - value.Length - 2;
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(headerLen);
            var written = RedisRespProtocol.WriteLPushCommandHeader(rented.AsSpan(0, headerLen), key, value.Length);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return resp.Kind == RedisRespReader.RespKind.Integer ? resp.IntegerValue : 0;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]?> LPopAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryLPopAsync(key, ct, out var fastTask))
            return fastTask;

        return LPopAsyncSlow(key, ct);
    }

    private async ValueTask<byte[]?> LPopAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPOP");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalBytesResponseAsync(conn, "LPOP", resp, copyPooled: false).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> ExpireAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        using var activity = StartCommandActivity("EXPIRE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var seconds = (long)Math.Ceiling(ttl.TotalSeconds);
        seconds = Math.Clamp(seconds, 0L, int.MaxValue);
        var secondsInt = (int)seconds;

        var len = RedisRespProtocol.GetExpireCommandLength(key, secondsInt);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteExpireCommand(rented.AsSpan(0, len), key, secondsInt);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return (await ReadIntegerResponseAsync(conn, "SISMEMBER", resp).ConfigureAwait(false)) == 1;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryHGetAsync(string key, string field, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        RecordCommandCall();
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            var cached = Volatile.Read(ref _hgetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetHGetCachedHeader(cached, key, field, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetHGetCommandLength(key, field);
                rented = conn.RentHeaderBuffer(len);
                var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _hgetHeaderCache,
                        new HGetHeaderCacheEntry(key, field, headerCopy),
                        null);
                }
            }

            if (!conn.TryExecuteAsync(
                header,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: headerBuffer))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapGetResponseAsync(conn, respTask, "HGET");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TrySetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan? ttl, CancellationToken ct, out ValueTask<bool> task)
    {
        RecordCommandCall();
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();

            if (ttlMs is null)
            {
                var len = RedisRespProtocol.GetSetCommandLength(key, value.Length, null);
                var headerLen = len - value.Length - 2;
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteSetCommandHeader(rented.AsSpan(0, headerLen), key, value.Length, null);
                if (!conn.TryExecuteAsync(
                    rented.AsMemory(0, written),
                    payload: value,
                    appendCrlf: true,
                    poolBulk: false,
                    ct,
                    out var respTask,
                    headerBuffer: rented))
                {
                    task = default;
                    return false;
                }

                rented = null; // returned by writer
                task = MapSetResponseAsync(conn, respTask);
                return true;
            }

            var fullLen = RedisRespProtocol.GetPSetExCommandLength(key, value.Length, ttlMs.Value);
            var ttlHeaderLen = fullLen - value.Length - 2;
            rented = conn.RentHeaderBuffer(ttlHeaderLen);
            var fullWritten = RedisRespProtocol.WritePSetExCommandHeader(rented.AsSpan(0, ttlHeaderLen), key, ttlMs.Value, value.Length);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, fullWritten),
                payload: value,
                appendCrlf: true,
                poolBulk: false,
                ct,
                out var respTaskTtl,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapSetResponseAsync(conn, respTaskTtl);
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null)
            {
                if (conn is not null)
                    conn.ReturnHeaderBuffer(rented);
            }
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryGetExLeaseAsync(string key, TimeSpan? ttl, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        RecordCommandCall();
        int? ttlMs = null;
        if (ttl is not null)
        {
            var ms = (long)ttl.Value.TotalMilliseconds;
            ttlMs = (int)Math.Clamp(ms, 1, int.MaxValue);
        }

        var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(conn, respTask, "GETEX");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryLPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        RecordCommandCall();

        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLPopResponseAsync(conn, respTask);
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisValueLease> LPopLeaseAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryLPopLeaseAsync(key, ct, out var fastTask))
            return fastTask;

        return LPopLeaseAsyncSlow(key, ct);
    }

    private async ValueTask<RedisValueLease> LPopLeaseAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LPOP");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalLeaseResponseAsync(conn, "LPOP", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]?> LIndexAsync(string key, long index, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryQueueLIndexFast(key, index, ct, out var fastTask))
            return fastTask;

        return LIndexAsyncSlow(key, index, ct);
    }

    private async ValueTask<byte[]?> LIndexAsyncSlow(string key, long index, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LINDEX");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();
        var len = RedisRespProtocol.GetLIndexCommandLength(key, index);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLIndexCommand(rented.AsSpan(0, len), key, index);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            return await ReadOptionalBytesResponseAsync(conn, "LINDEX", resp, copyPooled: true).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> LRangeAsync(string key, long start, long stop, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LRANGE");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetLRangeCommandLength(key, start, stop);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLRangeCommand(rented.AsSpan(0, len), key, start, stop);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer
            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<byte[]?>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "LRANGE", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.NullBulkString)
                    {
                        result[i] = null;
                        continue;
                    }

                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                    {
                        result[i] = items[i].Bulk;
                        continue;
                    }

                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "LRANGE", resp, returnBuffers: false).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Asynchronously releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _msetLengthsCache = null;
        _jsonSetHeaderCache = null;
        _hgetHeaderCache = null;
        _hsetHeaderCache = null;

        _muxOptionsChangeRegistration?.Dispose();
        try { _autoscaleCts.Cancel(); } catch { }
        if (_autoscaleTask is not null)
        {
            try { await _autoscaleTask.ConfigureAwait(false); } catch { }
        }

        foreach (var c in _conns)
            await c.DisposeAsync().ConfigureAwait(false);

        foreach (var c in _bulkConns)
            await c.DisposeAsync().ConfigureAwait(false);

        foreach (var c in _pubSubConns)
            await c.DisposeAsync().ConfigureAwait(false);

        foreach (var c in _blockingConns)
            await c.DisposeAsync().ConfigureAwait(false);
    }

    private RedisMultiplexedConnection CreateConnection(RuntimeConfig runtime, TimeSpan responseTimeout)
    {
        var mux = runtime.Multiplexer;
        return new(
            _factory,
            maxInFlight: mux.MaxInFlightPerConnection,
            coalesceWrites: mux.EnableCoalescedSocketWrites,
            enableSocketRespReader: mux.EnableSocketRespReader,
            useDedicatedLaneWorkers: mux.UseDedicatedLaneWorkers,
            maxBulkStringBytes: _connectionOptions.MaxBulkStringBytes,
            maxArrayDepth: _connectionOptions.MaxArrayDepth,
            responseTimeout: responseTimeout,
            coalescedWriteMaxBytes: mux.CoalescedWriteMaxBytes,
            coalescedWriteMaxSegments: mux.CoalescedWriteMaxSegments,
            coalescedWriteSmallCopyThresholdBytes: mux.CoalescedWriteSmallCopyThresholdBytes,
            enableAdaptiveCoalescing: mux.EnableAdaptiveCoalescing,
            adaptiveCoalescingLowDepth: mux.AdaptiveCoalescingLowDepth,
            adaptiveCoalescingHighDepth: mux.AdaptiveCoalescingHighDepth,
            adaptiveCoalescingMinWriteBytes: mux.AdaptiveCoalescingMinWriteBytes,
            adaptiveCoalescingMinSegments: mux.AdaptiveCoalescingMinSegments,
            adaptiveCoalescingMinSmallCopyThresholdBytes: mux.AdaptiveCoalescingMinSmallCopyThresholdBytes,
            coalescingEnterQueueDepth: mux.CoalescingEnterQueueDepth,
            coalescingExitQueueDepth: mux.CoalescingExitQueueDepth,
            coalescedWriteMaxOperations: mux.CoalescedWriteMaxOperations,
            coalescingSpinBudget: mux.CoalescingSpinBudget,
            recordLatencyStopwatchTicks: RecordAutoscaleLatencyStopwatchTicks,
            shouldRecordLatency: ShouldRecordAutoscaleLatency,
            shouldRecordRuntimeTelemetry: IsCommandInstrumentationEnabled);
    }

    private RedisMultiplexedConnection CreateFastConnection()
    {
        var runtime = ReadRuntimeConfig();
        return CreateConnection(runtime, runtime.Multiplexer.ResponseTimeout);
    }

    private RedisMultiplexedConnection CreateBulkConnection()
    {
        var runtime = ReadRuntimeConfig();
        return CreateConnection(runtime, runtime.BulkLaneResponseTimeout);
    }

    private RedisMultiplexedConnection CreatePubSubConnection()
    {
        var runtime = ReadRuntimeConfig();
        return CreateConnection(runtime, runtime.Multiplexer.ResponseTimeout);
    }

    private RedisMultiplexedConnection CreateBlockingConnection()
    {
        var runtime = ReadRuntimeConfig();
        return CreateConnection(runtime, runtime.Multiplexer.ResponseTimeout);
    }

    private void RebuildLanesUnsafe()
    {
        // Fast group remains shared read/write and is the only autoscaled group.
        _readConns = _conns;
        _writeConns = _conns;

        // Bulk group is fixed-size and isolated from autoscaler pressure signals.
        _bulkReadConns = _bulkConns;
        _bulkWriteConns = _bulkConns;
        _pubSubReadConns = _pubSubConns;
        _pubSubWriteConns = _pubSubConns;
        _blockingReadConns = _blockingConns;
        _blockingWriteConns = _blockingConns;
    }

    private async Task AutoscaleLoopAsync()
    {
        while (!_autoscaleCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_autoscaleSampleInterval, _autoscaleCts.Token).ConfigureAwait(false);
                EvaluateAutoscale();
            }
            catch (OperationCanceledException) when (_autoscaleCts.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // autoscaler is best-effort; command execution remains primary
            }
        }
    }

    private void EvaluateAutoscale()
    {
        if (!_autoscaleEnabled)
        {
            var disabledConnections = GetTotalConnectionCountUnsafe();
            Interlocked.Exchange(ref _highPressureStreakTicks, 0);
            Interlocked.Exchange(ref _lowPressureStreakTicks, 0);
            Volatile.Write(ref _lastTargetConnections, disabledConnections);
            LogTickTrace(
                action: "disabled",
                reason: "autoscaling-disabled",
                currentConnections: disabledConnections,
                targetConnections: disabledConnections,
                highSignals: 0,
                avgInflightUtil: 0d,
                maxInflightUtil: 0d,
                avgQueueDepth: 0d,
                maxQueueDepth: 0,
                timeoutRatePerSec: 0d,
                p95Ms: 0d,
                p99Ms: 0d,
                unhealthyConnections: 0,
                reconnectFailureRatePerSec: 0d,
                scaleEventsInCurrentMinute: Volatile.Read(ref _scaleEventsInCurrentMinute),
                maxScaleEventsPerMinute: _maxScaleEventsPerMinute,
                upCooldownActive: false,
                downCooldownActive: false,
                upWindowSatisfied: false,
                downWindowSatisfied: false,
                frozen: false,
                frozenUntilUtc: null);
            return;
        }

        var conns = _conns;
        if (conns.Length == 0)
            return;
        var currentConnections = GetTotalConnectionCountUnsafe();

        var now = Stopwatch.GetTimestamp();
        var minuteWindowTicks = ScaleEventWindowStopwatchTicks;
        var currentWindowStart = Interlocked.Read(ref _scaleEventWindowStartTicks);
        if (currentWindowStart != 0 && now - currentWindowStart >= minuteWindowTicks)
        {
            Interlocked.Exchange(ref _scaleEventWindowStartTicks, now);
            Volatile.Write(ref _scaleEventsInCurrentMinute, 0);
            Volatile.Write(ref _flapToggleCount, 0);
        }
        var maxInflight = 0;
        var inflightSum = 0;
        var maxInflightUtil = 0d;
        var maxQueueDepth = 0;
        var queueDepthSum = 0;
        var timeoutTotal = 0L;
        var failureTotal = 0L;
        var unhealthyConnections = 0;
        for (var i = 0; i < conns.Length; i++)
        {
            var c = conns[i];
            var inFlight = c.InFlightCount;
            var queue = c.WriteQueueDepth;
            var laneMaxInFlight = c.MaxInFlight;
            maxInflight = Math.Max(maxInflight, c.MaxInFlight);
            inflightSum += inFlight;
            queueDepthSum += queue;
            maxQueueDepth = Math.Max(maxQueueDepth, queue);
            timeoutTotal += c.ResponseTimeoutCount;
            failureTotal += c.FailureCount;
            if (!c.IsHealthy)
                unhealthyConnections++;
            if (laneMaxInFlight > 0)
                maxInflightUtil = Math.Max(maxInflightUtil, (double)inFlight / laneMaxInFlight);
        }

        var avgInflightUtil = maxInflight == 0 ? 0d : (double)inflightSum / (conns.Length * maxInflight);
        var avgQueueDepth = conns.Length == 0 ? 0d : (double)queueDepthSum / conns.Length;

        var lastTicks = Interlocked.Read(ref _lastTimeoutSampleTicks);
        var lastCount = Interlocked.Read(ref _lastTimeoutSampleCount);
        var elapsedSec = lastTicks == 0
            ? _autoscaleSampleInterval.TotalSeconds
            : Math.Max(0.001, (now - lastTicks) / (double)Stopwatch.Frequency);
        var timeoutRatePerSec = Math.Max(0, timeoutTotal - lastCount) / elapsedSec;
        Interlocked.Exchange(ref _lastTimeoutSampleCount, timeoutTotal);
        Interlocked.Exchange(ref _lastTimeoutSampleTicks, now);

        var lastFailureTicks = Interlocked.Read(ref _lastFailureSampleTicks);
        var lastFailureCount = Interlocked.Read(ref _lastFailureSampleCount);
        var failureElapsedSec = lastFailureTicks == 0
            ? _autoscaleSampleInterval.TotalSeconds
            : Math.Max(0.001, (now - lastFailureTicks) / (double)Stopwatch.Frequency);
        var reconnectFailureRatePerSec = Math.Max(0, failureTotal - lastFailureCount) / failureElapsedSec;
        Interlocked.Exchange(ref _lastFailureSampleCount, failureTotal);
        Interlocked.Exchange(ref _lastFailureSampleTicks, now);

        var (p95Ms, p99Ms) = GetRollingLatencyPercentiles();
        var highSignals = 0;
        if (avgInflightUtil >= _scaleUpInflightUtilization) highSignals++;
        if (maxQueueDepth >= _scaleUpQueueDepthThreshold) highSignals++;
        if (avgQueueDepth >= Math.Max(1, _scaleUpQueueDepthThreshold / 2.0)) highSignals++;
        if (timeoutRatePerSec >= _scaleUpTimeoutRatePerSecThreshold) highSignals++;
        if (p99Ms >= _scaleUpP99LatencyMsThreshold) highSignals++;
        Volatile.Write(ref _lastHighSignalCount, highSignals);
        Volatile.Write(ref _lastAvgInflightUtilization, avgInflightUtil);
        Volatile.Write(ref _lastAvgQueueDepth, avgQueueDepth);
        Volatile.Write(ref _lastMaxQueueDepth, maxQueueDepth);
        Volatile.Write(ref _lastTimeoutRatePerSec, timeoutRatePerSec);
        Volatile.Write(ref _lastRollingP95Ms, p95Ms);
        Volatile.Write(ref _lastRollingP99Ms, p99Ms);
        Volatile.Write(ref _lastReconnectFailureRatePerSec, reconnectFailureRatePerSec);
        Volatile.Write(ref _lastUnhealthyConnections, unhealthyConnections);

        var highPressure = highSignals >= 2;
        var lowPressure =
            avgInflightUtil <= _scaleDownInflightUtilization &&
            avgQueueDepth < 0.5 &&
            maxQueueDepth <= 1 &&
            timeoutRatePerSec <= 0.0 &&
            unhealthyConnections == 0 &&
            p95Ms <= _scaleDownP95LatencyMsThreshold;

        var reconnectStorm = reconnectFailureRatePerSec >= _reconnectStormFailureRatePerSecThreshold;
        if (reconnectStorm)
        {
            FreezeAutoscaler("reconnect-storm", now);
        }

        var isFrozen = TryGetFreezeState(now, out var frozenReason, out var frozenUntilUtc);
        var sampleTicks = ToStopwatchTicks(_autoscaleSampleInterval);
        var highStreakTicks = Interlocked.Read(ref _highPressureStreakTicks);
        var lowStreakTicks = Interlocked.Read(ref _lowPressureStreakTicks);
        var upCooldownActive = now - Interlocked.Read(ref _lastScaleUpTicks) < ToStopwatchTicks(_scaleUpCooldown);
        var downCooldownActive = now - Interlocked.Read(ref _lastScaleDownTicks) < ToStopwatchTicks(_scaleDownCooldown);
        if (highPressure)
        {
            Interlocked.Exchange(ref _lowPressureStreakTicks, 0);
            Interlocked.Add(ref _highPressureStreakTicks, sampleTicks);
            highStreakTicks = Interlocked.Read(ref _highPressureStreakTicks);
            lowStreakTicks = 0;
        }
        else if (lowPressure)
        {
            Interlocked.Exchange(ref _highPressureStreakTicks, 0);
            Interlocked.Add(ref _lowPressureStreakTicks, sampleTicks);
            lowStreakTicks = Interlocked.Read(ref _lowPressureStreakTicks);
            highStreakTicks = 0;
        }
        else
        {
            Interlocked.Exchange(ref _highPressureStreakTicks, 0);
            Interlocked.Exchange(ref _lowPressureStreakTicks, 0);
            highStreakTicks = 0;
            lowStreakTicks = 0;
        }

        var upWindowSatisfied = highStreakTicks >= ToStopwatchTicks(_scaleUpWindow);
        var downWindowSatisfied = lowStreakTicks >= ToStopwatchTicks(_scaleDownWindow);
        var action = "hold";
        var reason = "steady-state";
        var targetConnections = currentConnections;

        if (isFrozen)
        {
            reason = frozenReason;
            Volatile.Write(ref _lastTargetConnections, targetConnections);
            LogTickTrace(
                action,
                reason,
                currentConnections,
                targetConnections,
                highSignals,
                avgInflightUtil,
                maxInflightUtil,
                avgQueueDepth,
                maxQueueDepth,
                timeoutRatePerSec,
                p95Ms,
                p99Ms,
                unhealthyConnections,
                reconnectFailureRatePerSec,
                Volatile.Read(ref _scaleEventsInCurrentMinute),
                _maxScaleEventsPerMinute,
                upCooldownActive,
                downCooldownActive,
                upWindowSatisfied,
                downWindowSatisfied,
                true,
                frozenUntilUtc);
            return;
        }

        var emergencyEligible =
            timeoutRatePerSec >= _emergencyScaleUpTimeoutRatePerSecThreshold &&
            !upCooldownActive &&
            currentConnections < _maxConnections &&
            unhealthyConnections < conns.Length;
        if (emergencyEligible)
        {
            var emergencyReason = $"emergency-timeout-spike:{timeoutRatePerSec:F2}/s";
            if (_autoscaleAdvisorMode)
            {
                action = "up(advisor)";
                reason = emergencyReason;
                targetConnections = currentConnections + 1;
                LogDecision(action, reason, currentConnections, targetConnections, highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
            }
            else if (!CanScaleEventNow(now, out var blockReason))
            {
                reason = blockReason;
            }
            else
            {
                action = "up";
                reason = emergencyReason;
                targetConnections = Math.Min(_maxConnections, currentConnections + 1);
                ScaleUp(reason);
                RegisterScaleEvent(direction: "up", now);
                LogDecision(action, reason, currentConnections, targetConnections, highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
                Interlocked.Exchange(ref _lastScaleUpTicks, now);
            }

            Volatile.Write(ref _lastTargetConnections, targetConnections);
            LogTickTrace(
                action,
                reason,
                currentConnections,
                targetConnections,
                highSignals,
                avgInflightUtil,
                maxInflightUtil,
                avgQueueDepth,
                maxQueueDepth,
                timeoutRatePerSec,
                p95Ms,
                p99Ms,
                unhealthyConnections,
                reconnectFailureRatePerSec,
                Volatile.Read(ref _scaleEventsInCurrentMinute),
                _maxScaleEventsPerMinute,
                upCooldownActive,
                downCooldownActive,
                upWindowSatisfied,
                downWindowSatisfied,
                false,
                null);
            return;
        }

        if (highPressure &&
            upWindowSatisfied &&
            !upCooldownActive &&
            currentConnections < _maxConnections)
        {
            var scaleReason = BuildScaleReason(
                avgInflightUtil >= _scaleUpInflightUtilization,
                maxQueueDepth >= _scaleUpQueueDepthThreshold || avgQueueDepth >= Math.Max(1, _scaleUpQueueDepthThreshold / 2.0),
                timeoutRatePerSec >= _scaleUpTimeoutRatePerSecThreshold,
                p99Ms >= _scaleUpP99LatencyMsThreshold);
            if (_autoscaleAdvisorMode)
            {
                LogDecision("up(advisor)", scaleReason, currentConnections, currentConnections + 1, highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
                action = "up(advisor)";
                reason = scaleReason;
                targetConnections = currentConnections + 1;
            }
            else if (!CanScaleEventNow(now, out var blockReason))
            {
                reason = blockReason;
            }
            else
            {
                ScaleUp(scaleReason);
                RegisterScaleEvent(direction: "up", now);
                LogDecision("up", scaleReason, currentConnections, Math.Min(_maxConnections, currentConnections + 1), highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
                action = "up";
                reason = scaleReason;
                targetConnections = Math.Min(_maxConnections, currentConnections + 1);
                Interlocked.Exchange(ref _lastScaleUpTicks, now);
            }
            Interlocked.Exchange(ref _highPressureStreakTicks, 0);
            Volatile.Write(ref _lastTargetConnections, targetConnections);
            LogTickTrace(action, reason, currentConnections, targetConnections, highSignals, avgInflightUtil, maxInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms, unhealthyConnections, reconnectFailureRatePerSec, Volatile.Read(ref _scaleEventsInCurrentMinute), _maxScaleEventsPerMinute, upCooldownActive, downCooldownActive, upWindowSatisfied, downWindowSatisfied, false, null);
            return;
        }

        if (lowPressure &&
            downWindowSatisfied &&
            !downCooldownActive &&
            currentConnections > _minConnections)
        {
            if (_autoscaleAdvisorMode)
            {
                LogDecision("down(advisor)", "low-pressure", currentConnections, currentConnections - 1, highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
                action = "down(advisor)";
                reason = "low-pressure";
                targetConnections = currentConnections - 1;
            }
            else if (!CanScaleEventNow(now, out var blockReason))
            {
                reason = blockReason;
            }
            else
            {
                _ = ScaleDownAsync("low-pressure");
                RegisterScaleEvent(direction: "down", now);
                LogDecision("down", "low-pressure", currentConnections, Math.Max(_minConnections, currentConnections - 1), highSignals, avgInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms);
                action = "down";
                reason = "low-pressure";
                targetConnections = Math.Max(_minConnections, currentConnections - 1);
                Interlocked.Exchange(ref _lastScaleDownTicks, now);
            }
            Interlocked.Exchange(ref _lowPressureStreakTicks, 0);
            Volatile.Write(ref _lastTargetConnections, targetConnections);
            LogTickTrace(action, reason, currentConnections, targetConnections, highSignals, avgInflightUtil, maxInflightUtil, avgQueueDepth, maxQueueDepth, timeoutRatePerSec, p95Ms, p99Ms, unhealthyConnections, reconnectFailureRatePerSec, Volatile.Read(ref _scaleEventsInCurrentMinute), _maxScaleEventsPerMinute, upCooldownActive, downCooldownActive, upWindowSatisfied, downWindowSatisfied, false, null);
            return;
        }

        reason = BuildNoScaleReason(
            currentConnections,
            highPressure,
            lowPressure,
            unhealthyConnections,
            reconnectStorm,
            upWindowSatisfied,
            downWindowSatisfied,
            upCooldownActive,
            downCooldownActive);
        Volatile.Write(ref _lastTargetConnections, targetConnections);
        LogTickTrace(
            action,
            reason,
            currentConnections,
            targetConnections,
            highSignals,
            avgInflightUtil,
            maxInflightUtil,
            avgQueueDepth,
            maxQueueDepth,
            timeoutRatePerSec,
            p95Ms,
            p99Ms,
            unhealthyConnections,
            reconnectFailureRatePerSec,
            Volatile.Read(ref _scaleEventsInCurrentMinute),
            _maxScaleEventsPerMinute,
            upCooldownActive,
            downCooldownActive,
            upWindowSatisfied,
            downWindowSatisfied,
            false,
            null);
    }

    private void ApplyAutoscaleOptions(RedisMultiplexerOptions o)
    {
        RedisMultiplexedConnection[]? fastConnsToDispose = null;
        RedisMultiplexedConnection[]? bulkConnsToDispose = null;
        RedisMultiplexedConnection[]? pubSubConnsToDispose = null;
        RedisMultiplexedConnection[]? blockingConnsToDispose = null;
        lock (_connGate)
        {
            _autoscaleEnabled = o.EnableAutoscaling;
            _minConnections = Math.Max(1, Math.Min(o.MinConnections, o.MaxConnections));
            _maxConnections = Math.Max(_minConnections, o.MaxConnections);
            _configuredBulkLaneConnections = Math.Max(0, o.BulkLaneConnections);
            _configuredPubSubLaneConnections = Math.Max(0, o.PubSubLaneConnections);
            _configuredBlockingLaneConnections = Math.Max(0, o.BlockingLaneConnections);
            _autoAdjustBulkLanes = o.AutoAdjustBulkLanes;
            _bulkLaneTargetRatio = NormalizeBulkLaneTargetRatio(o.BulkLaneTargetRatio);
            var desiredBulkLaneResponseTimeout = ResolveBulkLaneResponseTimeout(o);
            var bulkTimeoutChanged = desiredBulkLaneResponseTimeout != _bulkLaneResponseTimeout;
            var configuredTotalConnections = Math.Max(1, o.Connections);
            var currentTotalConnections = GetTotalConnectionCountUnsafe();
            var desiredTotalConnections = _autoscaleEnabled
                ? Math.Clamp(currentTotalConnections, _minConnections, _maxConnections)
                : configuredTotalConnections;
            var laneBudget = ResolveLaneBudget(
                _configuredBulkLaneConnections,
                _configuredPubSubLaneConnections,
                _configuredBlockingLaneConnections,
                _autoAdjustBulkLanes,
                _bulkLaneTargetRatio,
                desiredTotalConnections);

            _bulkLaneResponseTimeout = desiredBulkLaneResponseTimeout;
            _autoscaleSampleInterval = o.AutoscaleSampleInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : o.AutoscaleSampleInterval;
            _scaleUpWindow = o.ScaleUpWindow <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : o.ScaleUpWindow;
            _scaleDownWindow = o.ScaleDownWindow <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : o.ScaleDownWindow;
            _scaleUpCooldown = o.ScaleUpCooldown <= TimeSpan.Zero ? TimeSpan.FromSeconds(20) : o.ScaleUpCooldown;
            _scaleDownCooldown = o.ScaleDownCooldown <= TimeSpan.Zero ? TimeSpan.FromSeconds(90) : o.ScaleDownCooldown;
            _scaleUpInflightUtilization = Math.Clamp(o.ScaleUpInflightUtilization, 0.10, 0.98);
            _scaleDownInflightUtilization = Math.Clamp(o.ScaleDownInflightUtilization, 0.01, 0.70);
            _scaleUpQueueDepthThreshold = Math.Max(1, o.ScaleUpQueueDepthThreshold);
            _scaleUpTimeoutRatePerSecThreshold = Math.Max(0.01, o.ScaleUpTimeoutRatePerSecThreshold);
            _scaleUpP99LatencyMsThreshold = Math.Max(1.0, o.ScaleUpP99LatencyMsThreshold);
            _scaleDownP95LatencyMsThreshold = Math.Max(0.5, o.ScaleDownP95LatencyMsThreshold);
            _autoscaleAdvisorMode = o.AutoscaleAdvisorMode;
            _emergencyScaleUpTimeoutRatePerSecThreshold = Math.Max(0.01, o.EmergencyScaleUpTimeoutRatePerSecThreshold);
            _scaleDownDrainTimeout = o.ScaleDownDrainTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : o.ScaleDownDrainTimeout;
            _maxScaleEventsPerMinute = Math.Max(1, o.MaxScaleEventsPerMinute);
            _flapToggleThreshold = Math.Max(2, o.FlapToggleThreshold);
            _autoscaleFreezeDuration = o.AutoscaleFreezeDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : o.AutoscaleFreezeDuration;
            _reconnectStormFailureRatePerSecThreshold = Math.Max(0.01, o.ReconnectStormFailureRatePerSecThreshold);

            fastConnsToDispose = ReconcileFastConnectionsUnsafe(laneBudget.FastConnections);
            bulkConnsToDispose = ReconcileBulkConnectionsUnsafe(laneBudget.BulkConnections, bulkTimeoutChanged);
            pubSubConnsToDispose = ReconcilePubSubConnectionsUnsafe(laneBudget.PubSubConnections);
            blockingConnsToDispose = ReconcileBlockingConnectionsUnsafe(laneBudget.BlockingConnections);

            RebuildLanesUnsafe();

            if (!_autoscaleEnabled)
            {
                Interlocked.Exchange(ref _autoscaleFrozenUntilTicks, 0);
                _freezeReason = null;
                _autoscaleLatencySampler.Reset();
                Volatile.Write(ref _scaleEventsInCurrentMinute, 0);
                Interlocked.Exchange(ref _scaleEventWindowStartTicks, 0);
                Volatile.Write(ref _flapToggleCount, 0);
                _lastScaleDirectionForFlap = null;
                Interlocked.Exchange(ref _lastScaleDirectionTicks, 0);
            }
        }

        if (fastConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(fastConnsToDispose);

        if (bulkConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(bulkConnsToDispose);

        if (pubSubConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(pubSubConnsToDispose);

        if (blockingConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(blockingConnsToDispose);
    }

    private RedisMultiplexedConnection[]? ReconcileFastConnectionsUnsafe(int desiredFastConnections)
    {
        desiredFastConnections = Math.Max(1, desiredFastConnections);

        if (_conns.Length == desiredFastConnections)
            return null;

        if (_conns.Length < desiredFastConnections)
        {
            var next = new RedisMultiplexedConnection[desiredFastConnections];
            Array.Copy(_conns, next, _conns.Length);
            for (var i = _conns.Length; i < next.Length; i++)
                next[i] = CreateFastConnection();
            _conns = next;
            return null;
        }

        var removedCount = _conns.Length - desiredFastConnections;
        var removed = new RedisMultiplexedConnection[removedCount];
        Array.Copy(_conns, desiredFastConnections, removed, 0, removedCount);

        var resized = new RedisMultiplexedConnection[desiredFastConnections];
        Array.Copy(_conns, resized, desiredFastConnections);
        _conns = resized;

        return removed;
    }

    private RedisMultiplexedConnection[]? ReconcileBulkConnectionsUnsafe(int desiredBulkLaneConnections, bool timeoutChanged)
    {
        RedisMultiplexedConnection[]? bulkConnsToDispose = null;

        if (_bulkConns.Length > 0 && (desiredBulkLaneConnections == 0 || timeoutChanged))
        {
            bulkConnsToDispose = _bulkConns;
            _bulkConns = Array.Empty<RedisMultiplexedConnection>();
        }

        if (desiredBulkLaneConnections > 0)
        {
            if (_bulkConns.Length == 0)
            {
                _bulkConns = new RedisMultiplexedConnection[desiredBulkLaneConnections];
                for (var i = 0; i < _bulkConns.Length; i++)
                    _bulkConns[i] = CreateBulkConnection();
            }
            else if (_bulkConns.Length != desiredBulkLaneConnections)
            {
                var oldBulk = _bulkConns;
                var nextBulk = new RedisMultiplexedConnection[desiredBulkLaneConnections];
                var retained = Math.Min(oldBulk.Length, nextBulk.Length);
                Array.Copy(oldBulk, nextBulk, retained);
                for (var i = retained; i < nextBulk.Length; i++)
                    nextBulk[i] = CreateBulkConnection();
                _bulkConns = nextBulk;

                if (oldBulk.Length > retained)
                {
                    var removed = new RedisMultiplexedConnection[oldBulk.Length - retained];
                    Array.Copy(oldBulk, retained, removed, 0, removed.Length);
                    bulkConnsToDispose = bulkConnsToDispose is null
                        ? removed
                        : [.. bulkConnsToDispose, .. removed];
                }
            }
        }

        _bulkLaneConnections = desiredBulkLaneConnections;
        return bulkConnsToDispose;
    }

    private RedisMultiplexedConnection[]? ReconcilePubSubConnectionsUnsafe(int desiredPubSubLaneConnections)
        => ReconcileRoleConnectionsUnsafe(ref _pubSubConns, desiredPubSubLaneConnections, CreatePubSubConnection);

    private RedisMultiplexedConnection[]? ReconcileBlockingConnectionsUnsafe(int desiredBlockingLaneConnections)
        => ReconcileRoleConnectionsUnsafe(ref _blockingConns, desiredBlockingLaneConnections, CreateBlockingConnection);

    private static RedisMultiplexedConnection[]? ReconcileRoleConnectionsUnsafe(
        ref RedisMultiplexedConnection[] currentConnections,
        int desiredConnections,
        Func<RedisMultiplexedConnection> connectionFactory)
    {
        desiredConnections = Math.Max(0, desiredConnections);

        if (currentConnections.Length == desiredConnections)
            return null;

        if (currentConnections.Length < desiredConnections)
        {
            var next = new RedisMultiplexedConnection[desiredConnections];
            Array.Copy(currentConnections, next, currentConnections.Length);
            for (var i = currentConnections.Length; i < next.Length; i++)
                next[i] = connectionFactory();
            currentConnections = next;
            return null;
        }

        var removedCount = currentConnections.Length - desiredConnections;
        var removed = new RedisMultiplexedConnection[removedCount];
        Array.Copy(currentConnections, desiredConnections, removed, 0, removedCount);

        if (desiredConnections == 0)
        {
            currentConnections = Array.Empty<RedisMultiplexedConnection>();
            return removed;
        }

        var resized = new RedisMultiplexedConnection[desiredConnections];
        Array.Copy(currentConnections, resized, desiredConnections);
        currentConnections = resized;

        return removed;
    }

    private static async Task DisposeConnectionsAsync(RedisMultiplexedConnection[] connections)
    {
        for (var i = 0; i < connections.Length; i++)
        {
            try
            {
                await connections[i].DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup; lane recreation already succeeded.
            }
        }
    }

    private void RecordLatencySample(double ms)
    {
        _autoscaleLatencySampler.Record(ms);
    }

    private void RecordAutoscaleLatencyStopwatchTicks(long elapsedStopwatchTicks)
    {
        if (!Volatile.Read(ref _autoscaleEnabled) || elapsedStopwatchTicks <= 0)
            return;

        RecordLatencySample(elapsedStopwatchTicks * 1000.0 / Stopwatch.Frequency);
    }

    private (double P95Ms, double P99Ms) GetRollingLatencyPercentiles()
        => _autoscaleLatencySampler.GetPercentiles();

    private bool ShouldRecordAutoscaleLatency()
        => Volatile.Read(ref _autoscaleEnabled);

    private sealed class RollingPercentileLatencySampler
    {
        private static readonly double[] BucketUpperBoundsMs =
        [
            0.125, 0.25, 0.5, 0.75, 1, 1.5, 2, 3,
            4, 5, 6, 8, 10, 12, 16, 20,
            25, 32, 40, 50, 64, 80, 100, 128,
            160, 200, 256, 320, 400, 512, 640, 800,
            1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000,
            6400, 8000, 10000, 12800, 16000, 20000, 30000, 60000
        ];

        private readonly int[] _slotBuckets;
        private readonly int[] _bucketCounts = new int[BucketUpperBoundsMs.Length];
        private long _nextSlotSequence;

        public RollingPercentileLatencySampler(int windowSize)
        {
            if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be a positive power of two.");

            _slotBuckets = GC.AllocateUninitializedArray<int>(windowSize);
            Array.Fill(_slotBuckets, -1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(double ms)
        {
            if (ms <= 0 || double.IsNaN(ms) || double.IsInfinity(ms))
                return;

            var bucket = GetBucketIndex(ms);
            var slot = (int)(Interlocked.Increment(ref _nextSlotSequence) - 1) & (_slotBuckets.Length - 1);
            var previousBucket = Interlocked.Exchange(ref _slotBuckets[slot], bucket);
            if ((uint)previousBucket < (uint)_bucketCounts.Length)
                Interlocked.Decrement(ref _bucketCounts[previousBucket]);
            Interlocked.Increment(ref _bucketCounts[bucket]);
        }

        public void Reset()
        {
            Array.Fill(_slotBuckets, -1);
            Array.Clear(_bucketCounts);
            Interlocked.Exchange(ref _nextSlotSequence, 0);
        }

        public (double P95Ms, double P99Ms) GetPercentiles()
        {
            var total = 0;
            for (var i = 0; i < _bucketCounts.Length; i++)
            {
                var count = Volatile.Read(ref _bucketCounts[i]);
                if (count > 0)
                    total += count;
            }

            if (total <= 0)
                return (0d, 0d);

            var p95Target = Math.Max(1, (int)Math.Ceiling(total * 0.95));
            var p99Target = Math.Max(1, (int)Math.Ceiling(total * 0.99));
            var cumulative = 0;
            var p95 = 0d;
            var p99 = 0d;

            for (var i = 0; i < _bucketCounts.Length; i++)
            {
                var count = Volatile.Read(ref _bucketCounts[i]);
                if (count <= 0)
                    continue;

                cumulative += count;
                if (p95 == 0d && cumulative >= p95Target)
                    p95 = BucketUpperBoundsMs[i];
                if (cumulative >= p99Target)
                {
                    p99 = BucketUpperBoundsMs[i];
                    break;
                }
            }

            if (p95 == 0d)
                p95 = BucketUpperBoundsMs[^1];
            if (p99 == 0d)
                p99 = BucketUpperBoundsMs[^1];

            return (p95, p99);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetBucketIndex(double ms)
        {
            for (var i = 0; i < BucketUpperBoundsMs.Length; i++)
            {
                if (ms <= BucketUpperBoundsMs[i])
                    return i;
            }

            return BucketUpperBoundsMs.Length - 1;
        }
    }

    private static string BuildScaleReason(bool inflight, bool queue, bool timeout, bool tail)
    {
        var reasons = new List<string>(4);
        if (inflight) reasons.Add("inflight");
        if (queue) reasons.Add("queue");
        if (timeout) reasons.Add("timeout");
        if (tail) reasons.Add("tail-latency");
        return reasons.Count == 0 ? "unknown" : string.Join("+", reasons);
    }

    private void ScaleUp(string reason)
    {
        RedisMultiplexedConnection[]? fastConnsToDispose = null;
        RedisMultiplexedConnection[]? bulkConnsToDispose = null;
        RedisMultiplexedConnection[]? pubSubConnsToDispose = null;
        RedisMultiplexedConnection[]? blockingConnsToDispose = null;
        lock (_connGate)
        {
            var currentConnections = GetTotalConnectionCountUnsafe();
            if (currentConnections >= _maxConnections)
                return;

            var targetConnections = currentConnections + 1;
            var laneBudget = ResolveLaneBudget(
                _configuredBulkLaneConnections,
                _configuredPubSubLaneConnections,
                _configuredBlockingLaneConnections,
                _autoAdjustBulkLanes,
                _bulkLaneTargetRatio,
                targetConnections);
            fastConnsToDispose = ReconcileFastConnectionsUnsafe(laneBudget.FastConnections);
            bulkConnsToDispose = ReconcileBulkConnectionsUnsafe(laneBudget.BulkConnections, timeoutChanged: false);
            pubSubConnsToDispose = ReconcilePubSubConnectionsUnsafe(laneBudget.PubSubConnections);
            blockingConnsToDispose = ReconcileBlockingConnectionsUnsafe(laneBudget.BlockingConnections);

            RebuildLanesUnsafe();
            Volatile.Write(ref _lastScaleEventTicks, Stopwatch.GetTimestamp());
            _lastScaleDirection = "up";
            _lastScaleReason = reason;
        }

        if (fastConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(fastConnsToDispose);

        if (bulkConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(bulkConnsToDispose);

        if (pubSubConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(pubSubConnsToDispose);

        if (blockingConnsToDispose is { Length: > 0 })
            _ = DisposeConnectionsAsync(blockingConnsToDispose);
    }

    private async Task ScaleDownAsync(string reason)
    {
        RedisMultiplexedConnection[]? fastConnsToDispose = null;
        RedisMultiplexedConnection[]? bulkConnsToDispose = null;
        RedisMultiplexedConnection[]? pubSubConnsToDispose = null;
        RedisMultiplexedConnection[]? blockingConnsToDispose = null;
        lock (_connGate)
        {
            var currentConnections = GetTotalConnectionCountUnsafe();
            if (currentConnections <= _minConnections)
                return;

            var targetConnections = currentConnections - 1;
            var laneBudget = ResolveLaneBudget(
                _configuredBulkLaneConnections,
                _configuredPubSubLaneConnections,
                _configuredBlockingLaneConnections,
                _autoAdjustBulkLanes,
                _bulkLaneTargetRatio,
                targetConnections);
            fastConnsToDispose = ReconcileFastConnectionsUnsafe(laneBudget.FastConnections);
            bulkConnsToDispose = ReconcileBulkConnectionsUnsafe(laneBudget.BulkConnections, timeoutChanged: false);
            pubSubConnsToDispose = ReconcilePubSubConnectionsUnsafe(laneBudget.PubSubConnections);
            blockingConnsToDispose = ReconcileBlockingConnectionsUnsafe(laneBudget.BlockingConnections);

            RebuildLanesUnsafe();
            Volatile.Write(ref _lastScaleEventTicks, Stopwatch.GetTimestamp());
            _lastScaleDirection = "down";
            _lastScaleReason = reason;
        }

        if (fastConnsToDispose is { Length: > 0 })
        {
            for (var i = 0; i < fastConnsToDispose.Length; i++)
            {
                await WaitForDrainAsync(fastConnsToDispose[i], _scaleDownDrainTimeout, _autoscaleCts.Token).ConfigureAwait(false);
                await fastConnsToDispose[i].DisposeAsync().ConfigureAwait(false);
            }
        }

        if (bulkConnsToDispose is not null)
        {
            for (var i = 0; i < bulkConnsToDispose.Length; i++)
                await bulkConnsToDispose[i].DisposeAsync().ConfigureAwait(false);
        }

        if (pubSubConnsToDispose is not null)
        {
            for (var i = 0; i < pubSubConnsToDispose.Length; i++)
                await pubSubConnsToDispose[i].DisposeAsync().ConfigureAwait(false);
        }

        if (blockingConnsToDispose is not null)
        {
            for (var i = 0; i < blockingConnsToDispose.Length; i++)
                await blockingConnsToDispose[i].DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask WaitForDrainAsync(RedisMultiplexedConnection conn, TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            return;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (conn.InFlightCount <= 0 && conn.WriteQueueDepth <= 0)
                return;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private void LogDecision(
        string action,
        string reason,
        int fromConnections,
        int toConnections,
        int highSignals,
        double avgInflightUtil,
        double avgQueueDepth,
        int maxQueueDepth,
        double timeoutRatePerSec,
        double p95Ms,
        double p99Ms)
    {
        LogAutoscalerDecision(
            _logger,
            action,
            fromConnections,
            toConnections,
            reason,
            highSignals,
            avgInflightUtil,
            avgQueueDepth,
            maxQueueDepth,
            timeoutRatePerSec,
            p95Ms,
            p99Ms);
    }

    private string BuildNoScaleReason(
        int currentConnections,
        bool highPressure,
        bool lowPressure,
        int unhealthyConnections,
        bool reconnectStorm,
        bool upWindowSatisfied,
        bool downWindowSatisfied,
        bool upCooldownActive,
        bool downCooldownActive)
    {
        if (reconnectStorm)
            return "blocked:reconnect-storm";

        if (unhealthyConnections > 0)
            return "blocked:unhealthy-lanes";

        if (highPressure)
        {
            if (currentConnections >= _maxConnections) return "blocked:max-connections";
            if (!upWindowSatisfied) return "blocked:scaleup-window";
            if (upCooldownActive) return "blocked:scaleup-cooldown";
        }

        if (lowPressure)
        {
            if (currentConnections <= _minConnections) return "blocked:min-connections";
            if (!downWindowSatisfied) return "blocked:scaledown-window";
            if (downCooldownActive) return "blocked:scaledown-cooldown";
        }

        return "blocked:pressure-insufficient";
    }

    private bool CanScaleEventNow(long nowTicks, out string blockedReason)
    {
        if (TryGetFreezeState(nowTicks, out blockedReason, out _))
            return false;

        var minuteWindowTicks = ScaleEventWindowStopwatchTicks;
        var windowStartTicks = Interlocked.Read(ref _scaleEventWindowStartTicks);
        if (windowStartTicks == 0 || nowTicks - windowStartTicks >= minuteWindowTicks)
        {
            Interlocked.Exchange(ref _scaleEventWindowStartTicks, nowTicks);
            Volatile.Write(ref _scaleEventsInCurrentMinute, 0);
            Volatile.Write(ref _flapToggleCount, 0);
        }

        if (Volatile.Read(ref _scaleEventsInCurrentMinute) >= _maxScaleEventsPerMinute)
        {
            FreezeAutoscaler("scale-rate-limit", nowTicks);
            blockedReason = "blocked:scale-rate-limit";
            return false;
        }

        blockedReason = string.Empty;
        return true;
    }

    private void RegisterScaleEvent(string direction, long nowTicks)
    {
        var minuteWindowTicks = ScaleEventWindowStopwatchTicks;
        var windowStartTicks = Interlocked.Read(ref _scaleEventWindowStartTicks);
        if (windowStartTicks == 0 || nowTicks - windowStartTicks >= minuteWindowTicks)
        {
            Interlocked.Exchange(ref _scaleEventWindowStartTicks, nowTicks);
            Volatile.Write(ref _scaleEventsInCurrentMinute, 0);
            Volatile.Write(ref _flapToggleCount, 0);
        }

        Interlocked.Increment(ref _scaleEventsInCurrentMinute);

        var previousDirection = _lastScaleDirectionForFlap;
        var previousDirectionTicks = Interlocked.Read(ref _lastScaleDirectionTicks);
        var isRapidToggle =
            !string.IsNullOrEmpty(previousDirection) &&
            !string.Equals(previousDirection, direction, StringComparison.Ordinal) &&
            previousDirectionTicks != 0 &&
            nowTicks - previousDirectionTicks <= minuteWindowTicks;

        if (isRapidToggle)
            Interlocked.Increment(ref _flapToggleCount);

        _lastScaleDirectionForFlap = direction;
        Interlocked.Exchange(ref _lastScaleDirectionTicks, nowTicks);

        if (Volatile.Read(ref _flapToggleCount) >= _flapToggleThreshold)
            FreezeAutoscaler("flap-detected", nowTicks);
    }

    private void FreezeAutoscaler(string reason, long nowTicks)
    {
        var freezeDuration = _autoscaleFreezeDuration <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : _autoscaleFreezeDuration;
        var untilTicks = nowTicks + ToStopwatchTicks(freezeDuration);
        var existingUntil = Interlocked.Read(ref _autoscaleFrozenUntilTicks);
        if (untilTicks > existingUntil)
            Interlocked.Exchange(ref _autoscaleFrozenUntilTicks, untilTicks);
        _freezeReason = reason;

        LogAutoscalerFreeze(
            _logger,
            reason,
            ToUtcTimestamp(Interlocked.Read(ref _autoscaleFrozenUntilTicks)));
    }

    private bool TryGetFreezeState(long nowTicks, out string reason, out DateTimeOffset? frozenUntilUtc)
    {
        var frozenUntilTicks = Interlocked.Read(ref _autoscaleFrozenUntilTicks);
        if (frozenUntilTicks <= 0)
        {
            reason = string.Empty;
            frozenUntilUtc = null;
            return false;
        }

        if (frozenUntilTicks > nowTicks)
        {
            reason = string.IsNullOrWhiteSpace(_freezeReason)
                ? "blocked:frozen"
                : $"blocked:frozen:{_freezeReason}";
            frozenUntilUtc = ToUtcTimestamp(frozenUntilTicks);
            return true;
        }

        Interlocked.Exchange(ref _autoscaleFrozenUntilTicks, 0);
        _freezeReason = null;
        reason = string.Empty;
        frozenUntilUtc = null;
        return false;
    }

    private void LogTickTrace(
        string action,
        string reason,
        int currentConnections,
        int targetConnections,
        int highSignals,
        double avgInflightUtil,
        double maxInflightUtil,
        double avgQueueDepth,
        int maxQueueDepth,
        double timeoutRatePerSec,
        double p95Ms,
        double p99Ms,
        int unhealthyConnections,
        double reconnectFailureRatePerSec,
        int scaleEventsInCurrentMinute,
        int maxScaleEventsPerMinute,
        bool upCooldownActive,
        bool downCooldownActive,
        bool upWindowSatisfied,
        bool downWindowSatisfied,
        bool frozen,
        DateTimeOffset? frozenUntilUtc)
    {
        LogAutoscalerTick(
            _logger,
            action,
            reason,
            currentConnections,
            targetConnections,
            highSignals,
            avgInflightUtil,
            maxInflightUtil,
            avgQueueDepth,
            maxQueueDepth,
            p95Ms,
            p99Ms,
            timeoutRatePerSec,
            unhealthyConnections,
            reconnectFailureRatePerSec,
            scaleEventsInCurrentMinute,
            maxScaleEventsPerMinute,
            upCooldownActive,
            downCooldownActive,
            upWindowSatisfied,
            downWindowSatisfied,
            frozen,
            frozenUntilUtc);
    }

    [LoggerMessage(
        EventId = 11000,
        Level = LogLevel.Warning,
        Message = "{OptionsName} options were normalized at runtime to enforce safe performance guardrails. Review configured values for invalid or out-of-range settings.")]
    private static partial void LogOptionsNormalized(ILogger logger, string optionsName);

    [LoggerMessage(
        EventId = 11001,
        Level = LogLevel.Information,
        Message = "Autoscaler decision: {Action} {From}->{To} reason={Reason} signals={Signals} inflight={Inflight:F3} avgQueue={AvgQueue:F2} maxQueue={MaxQueue} timeoutRate={TimeoutRate:F3}/s p95={P95:F2}ms p99={P99:F2}ms")]
    private static partial void LogAutoscalerDecision(
        ILogger logger,
        string action,
        int from,
        int to,
        string reason,
        int signals,
        double inflight,
        double avgQueue,
        int maxQueue,
        double timeoutRate,
        double p95,
        double p99);

    [LoggerMessage(
        EventId = 11002,
        Level = LogLevel.Warning,
        Message = "Autoscaler freeze: reason={Reason} until={UntilUtc:o}")]
    private static partial void LogAutoscalerFreeze(ILogger logger, string reason, DateTimeOffset untilUtc);

    [LoggerMessage(
        EventId = 11003,
        Level = LogLevel.Debug,
        Message = "Autoscaler tick: action={Action} reason={Reason} current={Current} target={Target} signals={Signals} inflight.avg={InflightAvg:F3} inflight.max={InflightMax:F3} queue.avg={QueueAvg:F2} queue.max={QueueMax} p95={P95:F2}ms p99={P99:F2}ms timeoutRate={TimeoutRate:F3}/s unhealthy={Unhealthy} reconnectFailureRate={ReconnectFailureRate:F3}/s events={Events}/{MaxEvents} cooldown.up={CooldownUp} cooldown.down={CooldownDown} window.up={WindowUp} window.down={WindowDown} frozen={Frozen} frozenUntil={FrozenUntilUtc}")]
    private static partial void LogAutoscalerTick(
        ILogger logger,
        string action,
        string reason,
        int current,
        int target,
        int signals,
        double inflightAvg,
        double inflightMax,
        double queueAvg,
        int queueMax,
        double p95,
        double p99,
        double timeoutRate,
        int unhealthy,
        double reconnectFailureRate,
        int events,
        int maxEvents,
        bool cooldownUp,
        bool cooldownDown,
        bool windowUp,
        bool windowDown,
        bool frozen,
        DateTimeOffset? frozenUntilUtc);

    /// <summary>
    /// Gets value.
    /// </summary>
    public RedisAutoscalerSnapshot GetAutoscalerSnapshot()
    {
        var eventTicks = Volatile.Read(ref _lastScaleEventTicks);
        var frozenUntilTicks = Interlocked.Read(ref _autoscaleFrozenUntilTicks);
        var nowTicks = Stopwatch.GetTimestamp();
        var frozen = frozenUntilTicks > nowTicks;
        var currentConnections = GetTotalConnectionCountUnsafe();
        var targetConnections = Volatile.Read(ref _lastTargetConnections);
        return new RedisAutoscalerSnapshot(
            Enabled: _autoscaleEnabled,
            CurrentConnections: currentConnections,
            TargetConnections: targetConnections <= 0 ? currentConnections : targetConnections,
            MinConnections: _minConnections,
            MaxConnections: _maxConnections,
            CurrentReadLanes: _readConns.Length,
            CurrentWriteLanes: _writeConns.Length,
            HighSignalCount: Volatile.Read(ref _lastHighSignalCount),
            AvgInflightUtilization: Volatile.Read(ref _lastAvgInflightUtilization),
            AvgQueueDepth: Volatile.Read(ref _lastAvgQueueDepth),
            MaxQueueDepth: Volatile.Read(ref _lastMaxQueueDepth),
            TimeoutRatePerSec: Volatile.Read(ref _lastTimeoutRatePerSec),
            RollingP95LatencyMs: Volatile.Read(ref _lastRollingP95Ms),
            RollingP99LatencyMs: Volatile.Read(ref _lastRollingP99Ms),
            UnhealthyConnections: Volatile.Read(ref _lastUnhealthyConnections),
            ReconnectFailureRatePerSec: Volatile.Read(ref _lastReconnectFailureRatePerSec),
            ScaleEventsInCurrentMinute: Volatile.Read(ref _scaleEventsInCurrentMinute),
            MaxScaleEventsPerMinute: _maxScaleEventsPerMinute,
            Frozen: frozen,
            FrozenUntilUtc: frozen ? ToUtcTimestamp(frozenUntilTicks) : null,
            FreezeReason: frozen ? _freezeReason : null,
            LastScaleEventUtc: eventTicks == 0 ? null : ToUtcTimestamp(eventTicks),
            LastScaleDirection: _lastScaleDirection,
            LastScaleReason: _lastScaleReason);
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public IReadOnlyList<RedisMuxLaneSnapshot> GetMuxLaneSnapshots()
    {
        var fastGroup = new LaneGroupSnapshot("fast", _conns, _readConns, _writeConns);
        var bulkGroup = new LaneGroupSnapshot("bulk", _bulkConns, _bulkReadConns, _bulkWriteConns);
        var pubSubGroup = new LaneGroupSnapshot("pubsub", _pubSubConns, _pubSubReadConns, _pubSubWriteConns);
        var blockingGroup = new LaneGroupSnapshot("blocking", _blockingConns, _blockingReadConns, _blockingWriteConns);
        var total = fastGroup.Connections.Length + bulkGroup.Connections.Length + pubSubGroup.Connections.Length + blockingGroup.Connections.Length;
        if (total == 0)
            return Array.Empty<RedisMuxLaneSnapshot>();

        var lanes = new RedisMuxLaneSnapshot[total];
        var index = 0;
        index = FillLaneGroupSnapshots(fastGroup, includeGroupPrefix: false, lanes, index);
        index = FillLaneGroupSnapshots(bulkGroup, includeGroupPrefix: true, lanes, index);
        index = FillLaneGroupSnapshots(pubSubGroup, includeGroupPrefix: true, lanes, index);
        _ = FillLaneGroupSnapshots(blockingGroup, includeGroupPrefix: true, lanes, index);
        return lanes;
    }

    private readonly record struct LaneGroupSnapshot(
        string Name,
        RedisMultiplexedConnection[] Connections,
        RedisMultiplexedConnection[] ReadConnections,
        RedisMultiplexedConnection[] WriteConnections);

    private static int FillLaneGroupSnapshots(
        LaneGroupSnapshot group,
        bool includeGroupPrefix,
        RedisMuxLaneSnapshot[] destination,
        int startIndex)
    {
        for (var i = 0; i < group.Connections.Length; i++)
        {
            var conn = group.Connections[i];
            var usage = conn.CaptureMuxLaneUsageSnapshot();
            var maxInFlight = usage.MaxInFlight;
            var utilization = maxInFlight <= 0 ? 0d : (double)usage.InFlight / maxInFlight;

            destination[startIndex + i] = new RedisMuxLaneSnapshot(
                LaneIndex: startIndex + i,
                ConnectionId: conn.ConnectionId,
                Role: ResolveLaneRole(
                    conn,
                    group.ReadConnections,
                    group.WriteConnections,
                    includeGroupPrefix ? group.Name : null),
                WriteQueueDepth: conn.WriteQueueDepth,
                InFlight: usage.InFlight,
                MaxInFlight: maxInFlight,
                InFlightUtilization: utilization,
                BytesSent: usage.BytesSent,
                BytesReceived: usage.BytesReceived,
                Operations: usage.Operations,
                Failures: usage.Failures,
                Responses: usage.Responses,
                OrphanedResponses: usage.OrphanedResponses,
                ResponseSequenceMismatches: usage.ResponseSequenceMismatches,
                TransportResets: usage.TransportResets,
                Healthy: conn.IsHealthy);
        }

        return startIndex + group.Connections.Length;
    }

    private static string ResolveLaneRole(
        RedisMultiplexedConnection lane,
        RedisMultiplexedConnection[] readConns,
        RedisMultiplexedConnection[] writeConns,
        string? groupPrefix = null)
    {
        var isRead = ContainsConnection(readConns, lane);
        var isWrite = ContainsConnection(writeConns, lane);

        var role = "unassigned";
        if (isRead && isWrite)
            role = "read-write";
        else if (isRead)
            role = "read";
        else if (isWrite)
            role = "write";

        return string.IsNullOrEmpty(groupPrefix)
            ? role
            : $"{groupPrefix}-{role}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsConnection(RedisMultiplexedConnection[] lanes, RedisMultiplexedConnection candidate)
    {
        for (var i = 0; i < lanes.Length; i++)
        {
            if (ReferenceEquals(lanes[i], candidate))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectLaneIndex(int seed, int laneCount)
    {
        // Hot path optimization: use bitmask when lane count is power-of-two.
        if ((laneCount & (laneCount - 1)) == 0)
            return seed & (laneCount - 1);
        return seed % laneCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectAdjacentLaneIndex(int index, int laneCount)
    {
        if ((laneCount & (laneCount - 1)) == 0)
            return (index + 1) & (laneCount - 1);

        var next = index + 1;
        return next == laneCount ? 0 : next;
    }

    private RedisMultiplexedConnection Next()
    {
        var conns = _conns;
        if (conns.Length == 1)
            return conns[0];

        var idx = Interlocked.Increment(ref _rr) & int.MaxValue;
        return conns[SelectLaneIndex(idx, conns.Length)];
    }

    private RedisMultiplexedConnection NextRead()
    {
        var readConns = _readConns;
        var laneCount = readConns.Length;
        if (laneCount == 1)
            return readConns[0];

        // Power-of-two choices: pick the less-loaded read lane to reduce tail spikes.
        var idx = Interlocked.Increment(ref _readRr) & int.MaxValue;
        var aIndex = SelectLaneIndex(idx, laneCount);
        var bIndex = SelectAdjacentLaneIndex(aIndex, laneCount);
        var a = readConns[aIndex];
        var b = readConns[bIndex];

        var aScore = a.GetLaneSelectionScore();
        var bScore = b.GetLaneSelectionScore();
        return aScore <= bScore ? a : b;
    }

    private RedisMultiplexedConnection NextWrite()
    {
        var writeConns = _writeConns;
        var laneCount = writeConns.Length;
        if (laneCount == 1)
            return writeConns[0];

        // Power-of-two choices for writes to reduce queue hotspots and p99 tails.
        var idx = Interlocked.Increment(ref _writeRr) & int.MaxValue;
        var aIndex = SelectLaneIndex(idx, laneCount);
        var bIndex = SelectAdjacentLaneIndex(aIndex, laneCount);
        var a = writeConns[aIndex];
        var b = writeConns[bIndex];

        var aScore = a.GetLaneSelectionScore();
        var bScore = b.GetLaneSelectionScore();
        return aScore <= bScore ? a : b;
    }

    private RedisMultiplexedConnection NextBulk()
    {
        var conns = _bulkConns;
        if (conns.Length == 0)
            return Next();
        if (conns.Length == 1)
            return conns[0];

        var idx = Interlocked.Increment(ref _bulkRr) & int.MaxValue;
        return conns[SelectLaneIndex(idx, conns.Length)];
    }

    private RedisMultiplexedConnection NextBulkRead()
    {
        var readConns = _bulkReadConns;
        var laneCount = readConns.Length;
        if (laneCount == 0)
            return NextRead();
        if (laneCount == 1)
            return readConns[0];

        var idx = Interlocked.Increment(ref _bulkReadRr) & int.MaxValue;
        var aIndex = SelectLaneIndex(idx, laneCount);
        var bIndex = SelectAdjacentLaneIndex(aIndex, laneCount);
        var a = readConns[aIndex];
        var b = readConns[bIndex];

        var aScore = a.GetLaneSelectionScore();
        var bScore = b.GetLaneSelectionScore();
        return aScore <= bScore ? a : b;
    }

    private RedisMultiplexedConnection NextBulkWrite()
    {
        var writeConns = _bulkWriteConns;
        var laneCount = writeConns.Length;
        if (laneCount == 0)
            return NextWrite();
        if (laneCount == 1)
            return writeConns[0];

        var idx = Interlocked.Increment(ref _bulkWriteRr) & int.MaxValue;
        var aIndex = SelectLaneIndex(idx, laneCount);
        var bIndex = SelectAdjacentLaneIndex(aIndex, laneCount);
        var a = writeConns[aIndex];
        var b = writeConns[bIndex];

        var aScore = a.GetLaneSelectionScore();
        var bScore = b.GetLaneSelectionScore();
        return aScore <= bScore ? a : b;
    }

    private Activity? StartCommandActivity(string op)
    {
        var runtime = ReadRuntimeConfig();
        return RedisTracing.StartCommand(op, runtime.EnableCommandInstrumentation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordCommandCall()
    {
        if (IsCommandInstrumentationEnabled())
            RedisMetrics.CommandCalls.Add(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordCommandFailure()
    {
        if (IsCommandInstrumentationEnabled())
            RedisMetrics.CommandFailures.Add(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordCommandDuration(double elapsedMs)
    {
        if (IsCommandInstrumentationEnabled())
            RedisMetrics.CommandMs.Record(elapsedMs);
    }

    private static bool TryGetJsonSetCachedHeader(
        JsonSetHeaderCacheEntry? cache,
        string key,
        string path,
        int payloadLength,
        out ReadOnlyMemory<byte> header)
    {
        if (cache is not null &&
            cache.PayloadLength == payloadLength &&
            string.Equals(cache.Key, key, StringComparison.Ordinal) &&
            string.Equals(cache.Path, path, StringComparison.Ordinal))
        {
            header = cache.Header;
            return true;
        }

        header = default;
        return false;
    }

    private static bool TryGetHGetCachedHeader(
        HGetHeaderCacheEntry? cache,
        string key,
        string field,
        out ReadOnlyMemory<byte> header)
    {
        if (cache is not null &&
            string.Equals(cache.Key, key, StringComparison.Ordinal) &&
            string.Equals(cache.Field, field, StringComparison.Ordinal))
        {
            header = cache.Header;
            return true;
        }

        header = default;
        return false;
    }

    private static bool TryGetHSetCachedHeader(
        HSetHeaderCacheEntry? cache,
        string key,
        string field,
        int valueLength,
        out ReadOnlyMemory<byte> header)
    {
        if (cache is not null &&
            cache.ValueLength == valueLength &&
            string.Equals(cache.Key, key, StringComparison.Ordinal) &&
            string.Equals(cache.Field, field, StringComparison.Ordinal))
        {
            header = cache.Header;
            return true;
        }

        header = default;
        return false;
    }

    private bool TryQueueHGetLeaseFast(string key, string field, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkRead();
            var cached = Volatile.Read(ref _hgetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetHGetCachedHeader(cached, key, field, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetHGetCommandLength(key, field);
                rented = conn.RentHeaderBuffer(len);
                var written = RedisRespProtocol.WriteHGetCommand(rented.AsSpan(0, len), key, field);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _hgetHeaderCache,
                        new HGetHeaderCacheEntry(key, field, headerCopy),
                        null);
                }
            }

            if (!conn.TryExecuteAsync(
                header,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: headerBuffer))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapLeaseResponseAsync(conn, respTask, "HGET");
            return true;
        }
        finally
        {
            if (rented is not null && conn is not null)
                conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TryQueueGetExFast(string key, int? ttlMs, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetGetExCommandLength(key, ttlMs);
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteGetExCommand(rented.AsSpan(0, len), key, ttlMs);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapGetResponseAsync(conn, respTask, "GETEX");
            return true;
        }
        finally
        {
            if (rented is not null && conn is not null)
                conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TryQueueLIndexFast(string key, long index, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetLIndexCommandLength(key, index);
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLIndexCommand(rented.AsSpan(0, len), key, index);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapGetResponseAsync(conn, respTask, "LINDEX");
            return true;
        }
        finally
        {
            if (rented is not null && conn is not null)
                conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TryQueueJsonGetFast(string key, string? path, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapGetResponseAsync(conn, respTask, "JSON.GET");
            return true;
        }
        finally
        {
            if (rented is not null && conn is not null)
                conn.ReturnHeaderBuffer(rented);
        }
    }

    private (string Key, int ValueLen)[] RentMSetLengths(int length)
    {
        var arr = Interlocked.Exchange(ref _msetLengthsCache, null);
        if (arr is null || arr.Length < length)
            arr = new (string Key, int ValueLen)[length];
        return arr;
    }

    private void ReturnMSetLengths((string Key, int ValueLen)[]? lengths, int used)
    {
        if (lengths is null) return;

        used = Math.Clamp(used, 0, lengths.Length);
        for (var i = 0; i < used; i++)
            lengths[i] = default;

        const int MaxCachedLength = 1024;
        if (lengths.Length > MaxCachedLength) return;

        Interlocked.CompareExchange(ref _msetLengthsCache, lengths, null);
    }

    private sealed class JsonSetHeaderCacheEntry
    {
        public JsonSetHeaderCacheEntry(string key, string path, int payloadLength, byte[] header)
        {
            Key = key;
            Path = path;
            PayloadLength = payloadLength;
            Header = header;
        }

        public string Key { get; }
        public string Path { get; }
        public int PayloadLength { get; }
        public byte[] Header { get; }
    }

    private sealed class HGetHeaderCacheEntry
    {
        public HGetHeaderCacheEntry(string key, string field, byte[] header)
        {
            Key = key;
            Field = field;
            Header = header;
        }

        public string Key { get; }
        public string Field { get; }
        public byte[] Header { get; }
    }

    private sealed class HSetHeaderCacheEntry
    {
        public HSetHeaderCacheEntry(string key, string field, int valueLength, byte[] header)
        {
            Key = key;
            Field = field;
            ValueLength = valueLength;
            Header = header;
        }

        public string Key { get; }
        public string Field { get; }
        public int ValueLength { get; }
        public byte[] Header { get; }
    }

    private static ValueTask<byte[]?> MapGetResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            try
            {
                return new ValueTask<byte[]?>(MapGetResponse(respTask.Result));
            }
            catch (Exception ex)
            {
                return new ValueTask<byte[]?>(Task.FromException<byte[]?>(ex));
            }
        }

        return AwaitMapGetResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapGetResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapGetResponse(resp);
        }

        static byte[]? MapGetResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                RedisRespReader.RespKind.Error => throw new InvalidOperationException($"Redis error: {resp.Text}"),
                _ => throw new InvalidOperationException($"Unexpected GET response: {resp.Kind}")
            };
        }
    }

    private ValueTask<byte[]?> MapGetResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask,
        string op)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return new ValueTask<byte[]?>((byte[]?)null);
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
                return new ValueTask<byte[]?>(resp.Bulk);
            if (resp.Kind == RedisRespReader.RespKind.Error)
                return new ValueTask<byte[]?>(Task.FromException<byte[]?>(new InvalidOperationException($"Redis error: {resp.Text}")));
            return ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, op, resp);
        }

        return AwaitMapGetResponseAsync(this, conn, respTask, op);

        static async ValueTask<byte[]?> AwaitMapGetResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task,
            string op)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
                return resp.Bulk;
            if (resp.Kind == RedisRespReader.RespKind.Error)
                throw new InvalidOperationException($"Redis error: {resp.Text}");
            return await ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, op, resp).ConfigureAwait(false);
        }
    }

    private static ValueTask<RedisValueLease> MapLeaseResponseAsync(ValueTask<RedisRespReader.RespValue> respTask, string op)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<RedisValueLease>(MapLeaseResponse(respTask.Result, op));

        return AwaitMapLeaseResponseAsync(respTask, op);

        static async ValueTask<RedisValueLease> AwaitMapLeaseResponseAsync(ValueTask<RedisRespReader.RespValue> task, string op)
        {
            var resp = await task.ConfigureAwait(false);
            return MapLeaseResponse(resp, op);
        }

        static RedisValueLease MapLeaseResponse(RedisRespReader.RespValue resp, string op)
        {
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return RedisValueLease.Create(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);

            RedisRespReader.ReturnBuffers(resp);
            throw new InvalidOperationException($"Unexpected {op} response: {resp.Kind}");
        }
    }

    private ValueTask<RedisValueLease> MapLeaseResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask,
        string op)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return new ValueTask<RedisValueLease>(RedisValueLease.Null);
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return new ValueTask<RedisValueLease>(RedisValueLease.Create(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled));
            return ThrowUnexpectedResponseAndResetAsync<RedisValueLease>(conn, op, resp);
        }

        return AwaitMapLeaseResponseAsync(this, conn, respTask, op);

        static async ValueTask<RedisValueLease> AwaitMapLeaseResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task,
            string op)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return RedisValueLease.Null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString && resp.Bulk is not null)
                return RedisValueLease.Create(resp.Bulk, resp.BulkLength, pooled: resp.BulkIsPooled);
            return await ThrowUnexpectedResponseAndResetAsync<RedisValueLease>(conn, op, resp).ConfigureAwait(false);
        }
    }

    private static ValueTask<bool> MapSetResponseAsync(ValueTask<RedisRespReader.RespValue> respTask, byte[]? rented = null)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
            return new ValueTask<bool>(MapSetResponse(respTask.Result));
        }

        return AwaitMapSetResponseAsync(respTask, rented);

        static async ValueTask<bool> AwaitMapSetResponseAsync(ValueTask<RedisRespReader.RespValue> task, byte[]? rented)
        {
            try
            {
                var resp = await task.ConfigureAwait(false);
                return MapSetResponse(resp);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        static bool MapSetResponse(RedisRespReader.RespValue resp)
        {
            if (resp.Kind == RedisRespReader.RespKind.Error)
                throw new InvalidOperationException($"Redis error: {resp.Text}");

            return resp.Kind == RedisRespReader.RespKind.SimpleString &&
                   (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK");
        }
    }

    private ValueTask<bool> MapSetResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask,
        byte[]? rented = null)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);

            if (resp.Kind == RedisRespReader.RespKind.Error)
                return new ValueTask<bool>(Task.FromException<bool>(new InvalidOperationException($"Redis error: {resp.Text}")));

            if (resp.Kind == RedisRespReader.RespKind.SimpleString &&
                (ReferenceEquals(resp.Text, RedisRespReader.OkSimpleString) || resp.Text == "OK"))
            {
                return new ValueTask<bool>(true);
            }

            return ThrowUnexpectedResponseAndResetAsync<bool>(conn, "SET", resp);
        }

        return AwaitMapSetResponseAsync(this, conn, respTask, rented);

        static async ValueTask<bool> AwaitMapSetResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task,
            byte[]? rented)
        {
            try
            {
                var resp = await task.ConfigureAwait(false);
                return await ReadSetResponseAsync(conn, resp).ConfigureAwait(false);
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static ValueTask<bool> MapSIsMemberResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<bool>(MapSIsMemberResponse(respTask.Result));

        return AwaitMapSIsMemberResponseAsync(respTask);

        static async ValueTask<bool> AwaitMapSIsMemberResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapSIsMemberResponse(resp);
        }

        static bool MapSIsMemberResponse(RedisRespReader.RespValue resp)
            => resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
    }

    private ValueTask<bool> MapSIsMemberResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return new ValueTask<bool>(resp.IntegerValue == 1);
            return ThrowUnexpectedResponseAndResetAsync<bool>(conn, "SISMEMBER", resp);
        }

        return AwaitMapSIsMemberResponseAsync(this, conn, respTask);

        static async ValueTask<bool> AwaitMapSIsMemberResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return resp.IntegerValue == 1;
            return await ThrowUnexpectedResponseAndResetAsync<bool>(conn, "SISMEMBER", resp).ConfigureAwait(false);
        }
    }

    private ValueTask<long> MapIntegerResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask,
        string op)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return new ValueTask<long>(resp.IntegerValue);
            return ThrowUnexpectedResponseAndResetAsync<long>(conn, op, resp);
        }

        return AwaitMapIntegerResponseAsync(this, conn, respTask, op);

        static async ValueTask<long> AwaitMapIntegerResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task,
            string op)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return resp.IntegerValue;
            return await ThrowUnexpectedResponseAndResetAsync<long>(conn, op, resp).ConfigureAwait(false);
        }
    }

    private ValueTask<bool> MapDeleteResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return new ValueTask<bool>(resp.IntegerValue > 0);
            return ThrowUnexpectedResponseAndResetAsync<bool>(conn, "DEL", resp);
        }

        return AwaitMapDeleteResponseAsync(this, conn, respTask);

        static async ValueTask<bool> AwaitMapDeleteResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.Integer)
                return resp.IntegerValue > 0;
            return await ThrowUnexpectedResponseAndResetAsync<bool>(conn, "DEL", resp).ConfigureAwait(false);
        }
    }

    private static ValueTask<byte[]?> MapLPopResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<byte[]?>(MapLPopResponse(respTask.Result));

        return AwaitMapLPopResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapLPopResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapLPopResponse(resp);
        }

        static byte[]? MapLPopResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString => resp.Bulk,
                _ => null
            };
        }
    }

    private ValueTask<byte[]?> MapLPopResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return new ValueTask<byte[]?>((byte[]?)null);
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
                return new ValueTask<byte[]?>(resp.Bulk);
            return ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, "LPOP", resp);
        }

        return AwaitMapLPopResponseAsync(this, conn, respTask);

        static async ValueTask<byte[]?> AwaitMapLPopResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return null;
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
                return resp.Bulk;
            return await ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, "LPOP", resp).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryLPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetLPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(conn, respTask, "LPOP");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private static ValueTask<byte[]?> MapRPopResponseAsync(ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
            return new ValueTask<byte[]?>(MapRPopResponse(respTask.Result));

        return AwaitMapRPopResponseAsync(respTask);

        static async ValueTask<byte[]?> AwaitMapRPopResponseAsync(ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            return MapRPopResponse(resp);
        }

        static byte[]? MapRPopResponse(RedisRespReader.RespValue resp)
        {
            return resp.Kind switch
            {
                RedisRespReader.RespKind.BulkString => resp.BulkIsPooled
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk,
                RedisRespReader.RespKind.NullBulkString => null,
                _ => null
            };
        }
    }

    private ValueTask<byte[]?> MapRPopResponseAsync(
        RedisMultiplexedConnection conn,
        ValueTask<RedisRespReader.RespValue> respTask)
    {
        if (respTask.IsCompletedSuccessfully)
        {
            var resp = respTask.Result;
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
            {
                var value = resp.BulkIsPooled && resp.Bulk is not null
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk;
                return new ValueTask<byte[]?>(value);
            }

            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return new ValueTask<byte[]?>((byte[]?)null);

            return ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, "RPOP", resp);
        }

        return AwaitMapRPopResponseAsync(this, conn, respTask);

        static async ValueTask<byte[]?> AwaitMapRPopResponseAsync(
            RedisCommandExecutor executor,
            RedisMultiplexedConnection conn,
            ValueTask<RedisRespReader.RespValue> task)
        {
            var resp = await task.ConfigureAwait(false);
            if (resp.Kind == RedisRespReader.RespKind.BulkString)
            {
                return resp.BulkIsPooled && resp.Bulk is not null
                    ? resp.Bulk.AsSpan(0, resp.BulkLength).ToArray()
                    : resp.Bulk;
            }

            if (resp.Kind == RedisRespReader.RespKind.NullBulkString)
                return null;

            return await ThrowUnexpectedResponseAndResetAsync<byte[]?>(conn, "RPOP", resp).ConfigureAwait(false);
        }
    }

    // ========== List Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> RPushAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPUSH");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetRPushCommandLength(key, value.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPushCommand(rented.AsSpan(0, len), key, value.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "RPUSH", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> RPushManyAsync(string key, ReadOnlyMemory<byte>[] values, int count, CancellationToken ct)
    {
        if (count <= 0 || values.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(count), "RPUSH many requires at least one value.");
        if (count > values.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Count cannot exceed values.Length.");

        using var activity = StartCommandActivity("RPUSH");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var commandPrefixLen = RedisRespProtocol.GetRPushManyPrefixLength(key, count);
        var bulkPrefixTotalLen = 0;
        for (var i = 0; i < count; i++)
            bulkPrefixTotalLen += RedisRespProtocol.GetBulkLengthPrefixLength(values[i].Length);
        var headerBufferLen = commandPrefixLen + bulkPrefixTotalLen;

        byte[]? headerBuffer = null;
        ReadOnlyMemory<byte>[]? payloadArrayBuffer = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            headerBuffer = conn.RentHeaderBuffer(headerBufferLen);
            var written = RedisRespProtocol.WriteRPushManyPrefix(headerBuffer.AsSpan(0, commandPrefixLen), key, count);

            var payloadSegmentCount = checked(count * 3);
            payloadArrayBuffer = conn.RentPayloadArray(payloadSegmentCount);
            var payloadWriteCount = 0;
            var cursor = written;
            for (var i = 0; i < count; i++)
            {
                var bulkPrefixLen = RedisRespProtocol.WriteBulkLength(headerBuffer.AsSpan(cursor), values[i].Length);
                payloadArrayBuffer[payloadWriteCount++] = headerBuffer.AsMemory(cursor, bulkPrefixLen);
                payloadArrayBuffer[payloadWriteCount++] = values[i];
                payloadArrayBuffer[payloadWriteCount++] = CrlfMemory;
                cursor += bulkPrefixLen;
            }

            var resp = await conn.ExecuteAsync(
                headerBuffer.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                appendCrlfPerPayload: false,
                poolBulk: false,
                ct,
                headerBuffer: headerBuffer,
                payloads: payloadArrayBuffer,
                payloadCount: payloadWriteCount,
                payloadArrayBuffer: payloadArrayBuffer).ConfigureAwait(false);
            headerBuffer = null; // returned by writer
            payloadArrayBuffer = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "RPUSH", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (headerBuffer is not null && conn is not null) conn.ReturnHeaderBuffer(headerBuffer);
            if (payloadArrayBuffer is not null && conn is not null) conn.ReturnPayloadArray(payloadArrayBuffer);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]?> RPopAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryRPopAsync(key, ct, out var fastTask))
            return fastTask;

        return RPopAsyncSlow(key, ct);
    }

    private async ValueTask<byte[]?> RPopAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPOP");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadOptionalBytesResponseAsync(conn, "RPOP", resp, copyPooled: true).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryRPopAsync(string key, CancellationToken ct, out ValueTask<byte[]?> task)
    {
        RecordCommandCall();

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapRPopResponseAsync(conn, respTask);
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisValueLease> RPopLeaseAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryRPopLeaseAsync(key, ct, out var fastTask))
            return fastTask;

        return RPopLeaseAsyncSlow(key, ct);
    }

    private async ValueTask<RedisValueLease> RPopLeaseAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("RPOP");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadOptionalLeaseResponseAsync(conn, "RPOP", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryRPopLeaseAsync(string key, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetRPopCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulkWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteRPopCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapLeaseResponseAsync(conn, respTask, "RPOP");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> LLenAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TryLLenAsync(key, ct, out var fastTask))
            return fastTask;

        return LLenAsyncSlow(key, ct);
    }

    private async ValueTask<long> LLenAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("LLEN");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetLLenCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLLenCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "LLEN", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TryLLenAsync(string key, CancellationToken ct, out ValueTask<long> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetLLenCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteLLenCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapIntegerResponseAsync(conn, respTask, "LLEN");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    // ========== Set Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> SAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TrySAddAsync(key, member, ct, out var fastTask))
            return fastTask;

        return SAddAsyncSlow(key, member, ct);
    }

    private async ValueTask<long> SAddAsyncSlow(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetSAddCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSAddCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "SADD", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TrySAddAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<long> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetSAddCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSAddCommand(rented.AsSpan(0, len), key, member.Span);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapIntegerResponseAsync(conn, respTask, "SADD");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> SRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SREM");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetSRemCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSRemCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "SREM", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> SIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SISMEMBER");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetSIsMemberCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSIsMemberCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TrySIsMemberAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct, out ValueTask<bool> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetSIsMemberCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSIsMemberCommand(rented.AsSpan(0, len), key, member.Span);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapSIsMemberResponseAsync(conn, respTask);
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<byte[]?[]> SMembersAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SMEMBERS");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetSMembersCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSMembersCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<byte[]?>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "SMEMBERS", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                var result = new byte[]?[count];
                for (var i = 0; i < count; i++)
                {
                    if (items[i].Kind == RedisRespReader.RespKind.BulkString)
                    {
                        result[i] = items[i].Bulk;
                        continue;
                    }

                    if (items[i].Kind == RedisRespReader.RespKind.NullBulkString)
                    {
                        result[i] = null;
                        continue;
                    }

                    return await ThrowUnexpectedResponseAndResetAsync<byte[]?[]>(conn, "SMEMBERS", resp, returnBuffers: false).ConfigureAwait(false);
                }
                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<long> SCardAsync(string key, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && !_clusterRedirectsEnabled && TrySCardAsync(key, ct, out var fastTask))
            return fastTask;

        return SCardAsyncSlow(key, ct);
    }

    private async ValueTask<long> SCardAsyncSlow(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("SCARD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetSCardCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSCardCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null; // returned by writer

            return await ReadIntegerResponseAsync(conn, "SCARD", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private bool TrySCardAsync(string key, CancellationToken ct, out ValueTask<long> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetSCardCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteSCardCommand(rented.AsSpan(0, len), key);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null; // returned by writer
            task = MapIntegerResponseAsync(conn, respTask, "SCARD");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    // ========== Sorted Set Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZAddAsync(string key, double score, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var scoreText = FormatDouble(score);
        var len = RedisRespProtocol.GetZAddCommandLength(key, scoreText, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZAddCommand(rented.AsSpan(0, len), key, scoreText, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadIntegerResponseAsync(conn, "ZADD", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZRemAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZREM");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetZRemCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRemCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadIntegerResponseAsync(conn, "ZREM", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> ZCardAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZCARD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetZCardCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZCardCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadIntegerResponseAsync(conn, "ZCARD", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<double?> ZScoreAsync(string key, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZSCORE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetZScoreCommandLength(key, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZScoreCommand(rented.AsSpan(0, len), key, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.BulkString or RedisRespReader.RespKind.SimpleString => ParseDouble(resp),
                _ => throw new InvalidOperationException($"Unexpected ZSCORE response: {resp.Kind}")
            };
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long?> ZRankAsync(string key, ReadOnlyMemory<byte> member, bool descending, CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANK" : "ZRANK");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetZRankCommandLength(key, member.Length, descending);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRankCommand(rented.AsSpan(0, len), key, member.Span, descending);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind switch
            {
                RedisRespReader.RespKind.NullBulkString => null,
                RedisRespReader.RespKind.Integer => resp.IntegerValue,
                _ => throw new InvalidOperationException($"Unexpected ZRANK response: {resp.Kind}")
            };
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<double> ZIncrByAsync(string key, double increment, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        using var activity = StartCommandActivity("ZINCRBY");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var incrementText = FormatDouble(increment);
        var len = RedisRespProtocol.GetZIncrByCommandLength(key, incrementText, member.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextWrite();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZIncrByCommand(rented.AsSpan(0, len), key, incrementText, member.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind is RedisRespReader.RespKind.BulkString or RedisRespReader.RespKind.SimpleString
                ? ParseDouble(resp)
                : throw new InvalidOperationException($"Unexpected ZINCRBY response: {resp.Kind}");
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeWithScoresAsync(string key, long start, long stop, bool descending, CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANGE" : "ZRANGE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetZRangeWithScoresCommandLength(key, start, stop, descending);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRangeWithScoresCommand(rented.AsSpan(0, len), key, start, stop, descending);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGE", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var count = resp.ArrayLength;
                if (count == 0)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (count % 2 != 0)
                    return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGE", resp, returnBuffers: false).ConfigureAwait(false);

                var result = new (byte[] Member, double Score)[count / 2];
                var idx = 0;
                for (var i = 0; i < count; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGE", resp, returnBuffers: false).ConfigureAwait(false);

                    var member = items[i].Bulk ?? Array.Empty<byte>();
                    var score = ParseDouble(items[i + 1]);
                    result[idx++] = (member, score);
                }

                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<(byte[] Member, double Score)[]> ZRangeByScoreWithScoresAsync(
        string key,
        double min,
        double max,
        bool descending,
        long? offset,
        long? count,
        CancellationToken ct)
    {
        using var activity = StartCommandActivity(descending ? "ZREVRANGEBYSCORE" : "ZRANGEBYSCORE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var minText = FormatDouble(min);
        var maxText = FormatDouble(max);
        var len = RedisRespProtocol.GetZRangeByScoreWithScoresCommandLength(key, minText, maxText, descending, offset, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextRead();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteZRangeByScoreWithScoresCommand(rented.AsSpan(0, len), key, minText, maxText, descending, offset, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                if (resp.Kind is RedisRespReader.RespKind.NullArray)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                    return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGEBYSCORE", resp, returnBuffers: false).ConfigureAwait(false);

                var items = resp.ArrayItems;
                var itemCount = resp.ArrayLength;
                if (itemCount == 0)
                    return Array.Empty<(byte[] Member, double Score)>();

                if (itemCount % 2 != 0)
                    return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGEBYSCORE", resp, returnBuffers: false).ConfigureAwait(false);

                var result = new (byte[] Member, double Score)[itemCount / 2];
                var idx = 0;
                for (var i = 0; i < itemCount; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        return await ThrowUnexpectedResponseAndResetAsync<(byte[] Member, double Score)[]>(conn, "ZRANGEBYSCORE", resp, returnBuffers: false).ConfigureAwait(false);

                    var member = items[i].Bulk ?? Array.Empty<byte>();
                    var score = ParseDouble(items[i + 1]);
                    result[idx++] = (member, score);
                }

                return result;
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    // ========== JSON Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]?> JsonGetAsync(string key, string? path, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryQueueJsonGetFast(key, path, ct, out var fastTask))
            return fastTask;

        return JsonGetAsyncSlow(key, path, ct);
    }

    private async ValueTask<byte[]?> JsonGetAsyncSlow(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.GET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadOptionalBytesResponseAsync(conn, "JSON.GET", resp, copyPooled: true).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<RedisValueLease> JsonGetLeaseAsync(string key, string? path, CancellationToken ct)
    {
        if (!IsCommandInstrumentationEnabled() && TryJsonGetLeaseAsync(key, path, ct, out var fastTask))
            return fastTask;

        return JsonGetLeaseAsyncSlow(key, path, ct);
    }

    private async ValueTask<RedisValueLease> JsonGetLeaseAsyncSlow(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.GET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulk();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadOptionalLeaseResponseAsync(conn, "JSON.GET", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public bool TryJsonGetLeaseAsync(string key, string? path, CancellationToken ct, out ValueTask<RedisValueLease> task)
    {
        RecordCommandCall();
        var len = RedisRespProtocol.GetJsonGetCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulk();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonGetCommand(rented.AsSpan(0, len), key, path);
            if (!conn.TryExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                out var respTask,
                headerBuffer: rented))
            {
                task = default;
                return false;
            }

            rented = null;
            task = MapLeaseResponseAsync(conn, respTask, "JSON.GET");
            return true;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> JsonSetAsync(string key, string? path, ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.SET");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        path ??= ".";
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            var cached = Volatile.Read(ref _jsonSetHeaderCache);
            byte[]? headerBuffer;
            ReadOnlyMemory<byte> header;
            if (TryGetJsonSetCachedHeader(cached, key, path, json.Length, out header))
            {
                headerBuffer = null;
            }
            else
            {
                var len = RedisRespProtocol.GetJsonSetCommandLength(key, path, json.Length);
                var headerLen = len - json.Length - 2;
                rented = conn.RentHeaderBuffer(headerLen);
                var written = RedisRespProtocol.WriteJsonSetCommandHeader(rented.AsSpan(0, headerLen), key, path, json.Length);
                header = rented.AsMemory(0, written);
                headerBuffer = rented;

                // Install a single hot-path cache entry the first time JSON.SET is used.
                if (cached is null)
                {
                    var headerCopy = GC.AllocateUninitializedArray<byte>(written);
                    rented.AsSpan(0, written).CopyTo(headerCopy);
                    Interlocked.CompareExchange(
                        ref _jsonSetHeaderCache,
                        new JsonSetHeaderCacheEntry(key, path, json.Length, headerCopy),
                        null);
                }
            }

            var resp = await conn.ExecuteAsync(
                header,
                payload: json,
                appendCrlf: true,
                poolBulk: false,
                ct,
                headerBuffer: headerBuffer).ConfigureAwait(false);
            rented = null;

            if (resp.Kind == RedisRespReader.RespKind.Error)
                throw new InvalidOperationException($"Redis error: {resp.Text}");

            if (resp.Kind == RedisRespReader.RespKind.SimpleString)
                return true;

            return await ThrowUnexpectedResponseAndResetAsync<bool>(conn, "JSON.SET", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<bool> JsonSetLeaseAsync(string key, string? path, RedisValueLease json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSetAsync(key, path, json.Memory, ct);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> JsonDelAsync(string key, string? path, CancellationToken ct)
    {
        using var activity = StartCommandActivity("JSON.DEL");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetJsonDelCommandLength(key, path);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteJsonDelCommand(rented.AsSpan(0, len), key, path);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadIntegerResponseAsync(conn, "JSON.DEL", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    // ========== RediSearch / RedisBloom / RedisTimeSeries ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> FtCreateAsync(string index, string prefix, string[] fields, CancellationToken ct)
    {
        using var activity = StartCommandActivity("FT.CREATE");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetFtCreateCommandLength(index, prefix, fields);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteFtCreateCommand(rented.AsSpan(0, len), index, prefix, fields);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.SimpleString;
        }
        catch (Exception ex) when (IsRedisIndexAlreadyExists(ex))
        {
            return false;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string[]> FtSearchAsync(string index, string query, int? offset, int? count, CancellationToken ct)
    {
        using var activity = StartCommandActivity("FT.SEARCH");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetFtSearchCommandLength(index, query, offset, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteFtSearchCommand(rented.AsSpan(0, len), index, query, offset, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                try
                {
                    if (resp.Kind is RedisRespReader.RespKind.NullArray)
                        return Array.Empty<string>();

                    if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                        throw new InvalidOperationException($"Unexpected FT.SEARCH response: {resp.Kind}");

                    return ParseFtSearchDocumentIds(resp);
                }
                catch (InvalidOperationException ex)
                {
                    await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private static string[] ParseFtSearchDocumentIds(RedisRespReader.RespValue response)
    {
        if (response.Kind is RedisRespReader.RespKind.NullArray)
            return Array.Empty<string>();

        if (response.Kind is not RedisRespReader.RespKind.Array || response.ArrayItems is null)
            throw new InvalidOperationException($"Unexpected FT.SEARCH response: {response.Kind}");

        var ids = new List<string>(Math.Max(0, response.ArrayLength - 1));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ExtractFtSearchDocumentIds(response, ids, seen, depth: 0);
        return ids.Count == 0 ? Array.Empty<string>() : ids.ToArray();
    }

    private static void ExtractFtSearchDocumentIds(
        RedisRespReader.RespValue value,
        List<string> ids,
        HashSet<string> seen,
        int depth)
    {
        if (depth >= 12 || value.Kind is not RedisRespReader.RespKind.Array || value.ArrayItems is null)
            return;

        var items = value.ArrayItems;
        var length = value.ArrayLength;
        if (length == 0)
            return;

        // RESP2 canonical shape: [total, docId, [fields...], docId, [fields...]]
        if (items[0].Kind == RedisRespReader.RespKind.Integer)
        {
            for (var i = 1; i < length; i++)
            {
                if (TryReadRespText(items[i], out var docId))
                {
                    AddFtSearchDocumentId(docId, ids, seen);
                    continue;
                }

                if (TryExtractDocumentIdFromTuple(items[i], out docId))
                    AddFtSearchDocumentId(docId, ids, seen);
            }
        }

        // RESP3 map-based payloads can appear as flattened key/value arrays.
        for (var i = 0; i + 1 < length; i += 2)
        {
            if (!TryReadRespText(items[i], out var key))
                continue;

            var valueItem = items[i + 1];
            if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadRespText(valueItem, out var docId))
                    AddFtSearchDocumentId(docId, ids, seen);
                continue;
            }

            if (key.Equals("results", StringComparison.OrdinalIgnoreCase)
                || key.Equals("docs", StringComparison.OrdinalIgnoreCase)
                || key.Equals("documents", StringComparison.OrdinalIgnoreCase)
                || key.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                ExtractFtSearchDocumentIds(valueItem, ids, seen, depth + 1);
                continue;
            }

            if (valueItem.Kind == RedisRespReader.RespKind.Array)
                ExtractFtSearchDocumentIds(valueItem, ids, seen, depth + 1);
        }

        // RESP3 tuple-style entries may appear as [docId, [attrs...]] per result row.
        if (TryExtractDocumentIdFromTuple(value, out var tupleDocId))
            AddFtSearchDocumentId(tupleDocId, ids, seen);

        for (var i = 0; i < length; i++)
        {
            var child = items[i];
            if (child.Kind == RedisRespReader.RespKind.Array)
                ExtractFtSearchDocumentIds(child, ids, seen, depth + 1);
        }
    }

    private static bool TryExtractDocumentIdFromTuple(RedisRespReader.RespValue value, out string documentId)
    {
        documentId = string.Empty;

        if (value.Kind is not RedisRespReader.RespKind.Array || value.ArrayItems is null || value.ArrayLength == 0)
            return false;

        var items = value.ArrayItems;
        var hasAggregateTail = false;
        for (var i = 1; i < value.ArrayLength; i++)
        {
            if (items[i].Kind == RedisRespReader.RespKind.Array)
            {
                hasAggregateTail = true;
                break;
            }
        }

        if (!hasAggregateTail)
            return false;

        if (!TryReadRespText(items[0], out var first))
            return false;

        if (IsFtSearchMetadataKey(first))
            return false;

        documentId = first;
        return true;
    }

    private static void AddFtSearchDocumentId(string candidate, List<string> ids, HashSet<string> seen)
    {
        if (candidate.Length == 0 || IsFtSearchMetadataKey(candidate))
            return;

        if (seen.Add(candidate))
            ids.Add(candidate);
    }

    private static bool IsFtSearchMetadataKey(string text)
        => text.Equals("total_results", StringComparison.OrdinalIgnoreCase)
           || text.Equals("results", StringComparison.OrdinalIgnoreCase)
           || text.Equals("docs", StringComparison.OrdinalIgnoreCase)
           || text.Equals("documents", StringComparison.OrdinalIgnoreCase)
           || text.Equals("id", StringComparison.OrdinalIgnoreCase)
           || text.Equals("payload", StringComparison.OrdinalIgnoreCase)
           || text.Equals("score", StringComparison.OrdinalIgnoreCase)
           || text.Equals("scores", StringComparison.OrdinalIgnoreCase)
           || text.Equals("extra_attributes", StringComparison.OrdinalIgnoreCase)
           || text.Equals("attributes", StringComparison.OrdinalIgnoreCase)
           || text.Equals("sortkey", StringComparison.OrdinalIgnoreCase)
           || text.Equals("values", StringComparison.OrdinalIgnoreCase)
           || text.Equals("value", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadRespText(RedisRespReader.RespValue value, out string text)
    {
        if (value.Kind == RedisRespReader.RespKind.SimpleString)
        {
            text = value.Text ?? string.Empty;
            return true;
        }

        if (value.Kind == RedisRespReader.RespKind.BulkString)
        {
            text = Encoding.UTF8.GetString(value.Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(value));
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> BfAddAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        using var activity = StartCommandActivity("BF.ADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetBfAddCommandLength(key, item.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteBfAddCommand(rented.AsSpan(0, len), key, item.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> BfExistsAsync(string key, ReadOnlyMemory<byte> item, CancellationToken ct)
    {
        using var activity = StartCommandActivity("BF.EXISTS");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetBfExistsCommandLength(key, item.Length);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteBfExistsCommand(rented.AsSpan(0, len), key, item.Span);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.Integer && resp.IntegerValue == 1;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<bool> TsCreateAsync(string key, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.CREATE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetTsCreateCommandLength(key);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsCreateCommand(rented.AsSpan(0, len), key);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return resp.Kind == RedisRespReader.RespKind.SimpleString;
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<long> TsAddAsync(string key, long timestamp, double value, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.ADD");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var valueText = FormatDouble(value);
        var len = RedisRespProtocol.GetTsAddCommandLength(key, timestamp, valueText);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsAddCommand(rented.AsSpan(0, len), key, timestamp, valueText);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            return await ReadIntegerResponseAsync(conn, "TS.ADD", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    public async ValueTask<(long Timestamp, double Value)[]> TsRangeAsync(string key, long from, long to, CancellationToken ct)
    {
        using var activity = StartCommandActivity("TS.RANGE");
        activity?.SetTag("db.redis.key", key);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetTsRangeCommandLength(key, from, to);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteTsRangeCommand(rented.AsSpan(0, len), key, from, to);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;

            try
            {
                try
                {
                    if (resp.Kind is RedisRespReader.RespKind.NullArray)
                        return Array.Empty<(long Timestamp, double Value)>();

                    if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                        throw new InvalidOperationException($"Unexpected TS.RANGE response: {resp.Kind}");

                    var items = resp.ArrayItems;
                    var count = resp.ArrayLength;
                    if (count == 0)
                        return Array.Empty<(long Timestamp, double Value)>();

                    var result = new (long Timestamp, double Value)[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entry = items[i];
                        var entryItems = entry.ArrayItems;
                        var entryCount = entry.ArrayLength;
                        if (entry.Kind is not RedisRespReader.RespKind.Array || entryItems is null || entryCount < 2)
                            throw new InvalidOperationException($"Unexpected TS.RANGE sample kind: {entry.Kind}");

                        var timestamp = ParseLong(entryItems[0]);
                        var value = ParseDouble(entryItems[1]);
                        result[i] = (timestamp, value);
                    }

                    return result;
                }
                catch (InvalidOperationException ex)
                {
                    await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    // ========== Scan Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async IAsyncEnumerable<string> ScanAsync(
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        long cursor = 0;
        do
        {
            var (nextCursor, keys) = await ScanKeysPageAsync(cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var key in keys)
                yield return key;
        } while (cursor != 0);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async IAsyncEnumerable<byte[]> SScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanBytesPageAsync("SSCAN", key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    public async IAsyncEnumerable<(string Field, byte[] Value)> HScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanHashPageAsync(key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    public async IAsyncEnumerable<(byte[] Member, double Score)> ZScanAsync(
        string key,
        string? pattern = null,
        int pageSize = 128,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        long cursor = 0;
        do
        {
            var (nextCursor, items) = await ScanSortedSetPageAsync(key, cursor, pattern, pageSize, ct).ConfigureAwait(false);
            cursor = nextCursor;
            foreach (var item in items)
                yield return item;
        } while (cursor != 0);
    }

    private async ValueTask<(RedisMultiplexedConnection Connection, RedisRespReader.RespValue Response)> ExecuteScanPageAsync(
        string command,
        string? key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        using var activity = StartCommandActivity(command);
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        var len = RedisRespProtocol.GetScanCommandLength(command, key, cursor, pattern, count);
        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            rented = conn.RentHeaderBuffer(len);
            var written = RedisRespProtocol.WriteScanCommand(rented.AsSpan(0, len), command, key, cursor, pattern, count);
            var resp = await conn.ExecuteAsync(
                rented.AsMemory(0, written),
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: rented).ConfigureAwait(false);
            rented = null;
            return (conn, resp);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private async ValueTask<(long Cursor, string[] Keys)> ScanKeysPageAsync(
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var (conn, resp) = await ExecuteScanPageAsync("SCAN", null, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                    throw new InvalidOperationException($"Unexpected SCAN response: {resp.Kind}");

                var cursorValue = ParseCursor(resp.ArrayItems[0]);
                var itemsValue = resp.ArrayItems[1];
                if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                    return (cursorValue, Array.Empty<string>());

                if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected SCAN items kind: {itemsValue.Kind}");

                var items = itemsValue.ArrayItems;
                var itemCount = itemsValue.ArrayLength;
                var keys = new string[itemCount];
                for (var i = 0; i < itemCount; i++)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected SCAN item kind: {items[i].Kind}");
                    keys[i] = Encoding.UTF8.GetString(items[i].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(items[i]));
                }

                return (cursorValue, keys);
            }
            catch (InvalidOperationException ex)
            {
                await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, byte[][] Items)> ScanBytesPageAsync(
        string command,
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var (conn, resp) = await ExecuteScanPageAsync(command, key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                    throw new InvalidOperationException($"Unexpected {command} response: {resp.Kind}");

                var cursorValue = ParseCursor(resp.ArrayItems[0]);
                var itemsValue = resp.ArrayItems[1];
                if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                    return (cursorValue, Array.Empty<byte[]>());

                if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected {command} items kind: {itemsValue.Kind}");

                var items = itemsValue.ArrayItems;
                var itemCount = itemsValue.ArrayLength;
                var result = new byte[itemCount][];
                for (var i = 0; i < itemCount; i++)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected {command} item kind: {items[i].Kind}");
                    result[i] = items[i].Bulk ?? Array.Empty<byte>();
                }

                return (cursorValue, result);
            }
            catch (InvalidOperationException ex)
            {
                await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, (string Field, byte[] Value)[] Items)> ScanHashPageAsync(
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var (conn, resp) = await ExecuteScanPageAsync("HSCAN", key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                    throw new InvalidOperationException($"Unexpected HSCAN response: {resp.Kind}");

                var cursorValue = ParseCursor(resp.ArrayItems[0]);
                var itemsValue = resp.ArrayItems[1];
                if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                    return (cursorValue, Array.Empty<(string Field, byte[] Value)>());

                if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected HSCAN items kind: {itemsValue.Kind}");

                var items = itemsValue.ArrayItems;
                var itemCount = itemsValue.ArrayLength;
                if (itemCount % 2 != 0)
                    throw new InvalidOperationException("HSCAN returned an odd number of items.");

                var result = new (string Field, byte[] Value)[itemCount / 2];
                var idx = 0;
                for (var i = 0; i < itemCount; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected HSCAN field kind: {items[i].Kind}");
                    if (items[i + 1].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected HSCAN value kind: {items[i + 1].Kind}");

                    var field = Encoding.UTF8.GetString(items[i].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(items[i]));
                    var value = items[i + 1].Bulk ?? Array.Empty<byte>();
                    result[idx++] = (field, value);
                }

                return (cursorValue, result);
            }
            catch (InvalidOperationException ex)
            {
                await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    private async ValueTask<(long Cursor, (byte[] Member, double Score)[] Items)> ScanSortedSetPageAsync(
        string key,
        long cursor,
        string? pattern,
        int count,
        CancellationToken ct)
    {
        var (conn, resp) = await ExecuteScanPageAsync("ZSCAN", key, cursor, pattern, count, ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null || resp.ArrayLength < 2)
                    throw new InvalidOperationException($"Unexpected ZSCAN response: {resp.Kind}");

                var cursorValue = ParseCursor(resp.ArrayItems[0]);
                var itemsValue = resp.ArrayItems[1];
                if (itemsValue.Kind is RedisRespReader.RespKind.NullArray)
                    return (cursorValue, Array.Empty<(byte[] Member, double Score)>());

                if (itemsValue.Kind is not RedisRespReader.RespKind.Array || itemsValue.ArrayItems is null)
                    throw new InvalidOperationException($"Unexpected ZSCAN items kind: {itemsValue.Kind}");

                var items = itemsValue.ArrayItems;
                var itemCount = itemsValue.ArrayLength;
                if (itemCount % 2 != 0)
                    throw new InvalidOperationException("ZSCAN returned an odd number of items.");

                var result = new (byte[] Member, double Score)[itemCount / 2];
                var idx = 0;
                for (var i = 0; i < itemCount; i += 2)
                {
                    if (items[i].Kind is not RedisRespReader.RespKind.BulkString)
                        throw new InvalidOperationException($"Unexpected ZSCAN member kind: {items[i].Kind}");
                    var member = items[i].Bulk ?? Array.Empty<byte>();
                    var score = ParseDouble(items[i + 1]);
                    result[idx++] = (member, score);
                }

                return (cursorValue, result);
            }
            catch (InvalidOperationException ex)
            {
                await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            RedisRespReader.ReturnBuffers(resp);
        }
    }

    // ========== Server Commands ==========

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string> PingAsync(CancellationToken ct)
    {
        using var activity = StartCommandActivity("PING");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = Next();
            var cmd = RedisRespProtocol.PingCommand;
            var resp = await conn.ExecuteAsync(
                cmd,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: false,
                ct,
                headerBuffer: null).ConfigureAwait(false);

            return await ReadSimpleStringResponseAsync(conn, "PING", resp).ConfigureAwait(false);
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask<string[]> ModuleListAsync(CancellationToken ct)
    {
        using var activity = StartCommandActivity("MODULE");
        var sw = Stopwatch.StartNew();
        RecordCommandCall();

        byte[]? rented = null;
        RedisMultiplexedConnection? conn = null;
        try
        {
            conn = NextBulk();
            var cmd = RedisRespProtocol.ModuleListCommand;
            var resp = await conn.ExecuteAsync(
                cmd,
                payload: ReadOnlyMemory<byte>.Empty,
                appendCrlf: false,
                poolBulk: true,
                ct,
                headerBuffer: null).ConfigureAwait(false);

            try
            {
                try
                {
                    if (resp.Kind is RedisRespReader.RespKind.NullArray)
                        return Array.Empty<string>();

                    if (resp.Kind is not RedisRespReader.RespKind.Array || resp.ArrayItems is null)
                        throw new InvalidOperationException($"Unexpected MODULE LIST response: {resp.Kind}");

                    var items = resp.ArrayItems;
                    var count = resp.ArrayLength;
                    var result = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        // Each module is returned as an array with metadata
                        // We just extract the module name from the first element
                        var entry = items[i];
                        var moduleInfo = entry.ArrayItems;
                        var entryCount = entry.ArrayLength;
                        if (entry.Kind is not RedisRespReader.RespKind.Array || moduleInfo is null)
                            throw new InvalidOperationException($"Unexpected MODULE LIST item kind: {entry.Kind}");

                        if (entryCount > 1 && moduleInfo[1].Kind == RedisRespReader.RespKind.BulkString)
                        {
                            result[i] = Encoding.UTF8.GetString(moduleInfo[1].Bulk ?? Array.Empty<byte>(), 0, GetBulkLength(moduleInfo[1]));
                        }
                        else
                        {
                            result[i] = string.Empty;
                        }
                    }

                    return result;
                }
                catch (InvalidOperationException ex)
                {
                    await conn.ResetTransportAsync(ex).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                RedisRespReader.ReturnBuffers(resp);
            }
        }
        catch
        {
            RecordCommandFailure();
            throw;
        }
        finally
        {
            sw.Stop();
            RecordCommandDuration(sw.Elapsed.TotalMilliseconds);
            if (rented is not null && conn is not null) conn.ReturnHeaderBuffer(rented);
        }
    }

    private sealed record RuntimeConfig(
        RedisMultiplexerOptions Multiplexer,
        bool EnableCommandInstrumentation,
        TimeSpan BulkLaneResponseTimeout)
    {
        public static RuntimeConfig Empty { get; } = new(
            new RedisMultiplexerOptions(),
            EnableCommandInstrumentation: false,
            BulkLaneResponseTimeout: TimeSpan.FromSeconds(5));
    }
}






