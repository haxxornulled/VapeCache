using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Console.Stress;

namespace VapeCache.Console.Pos;

internal sealed class PosSearchLoadHostedService(
    IHostApplicationLifetime hostLifetime,
    IOptionsMonitor<PosSearchLoadOptions> loadOptionsMonitor,
    IOptionsMonitor<PosSearchDemoOptions> demoOptionsMonitor,
    PosCatalogSearchService searchService,
    IRedisCircuitBreakerState? breakerState,
    ILogger<PosSearchLoadHostedService> logger) : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loadOptions = loadOptionsMonitor.CurrentValue;
        if (!loadOptions.Enabled)
            return;

        var demoOptions = demoOptionsMonitor.CurrentValue;
        var workload = Normalize(loadOptions, demoOptions);

        await searchService.InitializeAsync(stoppingToken).ConfigureAwait(false);

        if (workload.EnableAutoRamp)
        {
            await ExecuteAutoRampAsync(workload, stoppingToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogInformation("==================================================");
            logger.LogInformation("  POS SEARCH LOAD - CACHE STAMPEDE SIMULATION");
            logger.LogInformation("==================================================");
            logger.LogInformation(
                "Duration={Duration} Concurrency={Concurrency} TargetShoppersPerSecond={TargetSps} HotQuery='{HotQuery}' (Hot={HotPct}% Cashier={CashierPct}% Upc={UpcPct}% Random={RandomPct}%)",
                workload.Duration,
                workload.Concurrency,
                workload.TargetShoppersPerSecond,
                workload.HotQuery,
                workload.HotQueryPercent,
                workload.CashierQueryPercent,
                workload.LookupUpcPercent,
                workload.RandomQueryPercent);

            var outcome = await RunLoadStepAsync(workload, stepLabel: "single", stoppingToken).ConfigureAwait(false);
            LogStepCompletion(outcome, isRampStep: false);
        }

        if (workload.StopHostOnCompletion)
        {
            logger.LogInformation("Stopping host after POS load completion.");
            hostLifetime.StopApplication();
        }
    }

    private async Task ExecuteAutoRampAsync(Workload workload, CancellationToken stoppingToken)
    {
        var steps = ParseRampSteps(workload.RampSteps, workload.TargetShoppersPerSecond);

        logger.LogInformation("==================================================");
        logger.LogInformation("  POS SEARCH LOAD - AUTO RAMP");
        logger.LogInformation("==================================================");
        logger.LogInformation(
            "RampSteps={RampSteps} StepDuration={StepDuration} Concurrency={Concurrency} HotQuery='{HotQuery}' Thresholds(MaxFailure={MaxFailure:F2}%, MaxP95={MaxP95:F2}ms, BreakerUnstable={BreakerUnstable})",
            string.Join(',', steps),
            workload.RampStepDuration,
            workload.Concurrency,
            workload.HotQuery,
            workload.MaxFailurePercent,
            workload.MaxP95Ms,
            workload.TreatOpenCircuitAsUnstable);

        var outcomes = new List<StepOutcome>(steps.Length);
        for (var i = 0; i < steps.Length; i++)
        {
            var targetSps = steps[i];
            var rampStep = workload with
            {
                Duration = workload.RampStepDuration,
                TargetShoppersPerSecond = targetSps
            };

            logger.LogInformation("Starting ramp step {Step}/{Total} TargetShoppersPerSecond={TargetSps}", i + 1, steps.Length, targetSps);
            var outcome = await RunLoadStepAsync(rampStep, stepLabel: $"ramp-{i + 1}", stoppingToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            LogStepCompletion(outcome, isRampStep: true);

            if (workload.StopOnFirstUnstable && !outcome.Assessment.IsStable)
            {
                logger.LogWarning("Stopping auto-ramp early at step {Step} because it is unstable.", i + 1);
                break;
            }
        }

        var bestStable = outcomes
            .Where(static outcome => outcome.Assessment.IsStable)
            .OrderByDescending(static outcome => outcome.TargetShoppersPerSecond)
            .ThenByDescending(static outcome => outcome.Snapshot.RequestsPerSecond)
            .FirstOrDefault();

        if (bestStable.TargetShoppersPerSecond > 0)
        {
            logger.LogInformation(
                "POS auto-ramp best stable step: Target={TargetSps}/s Achieved={Rate:F0}/s P95={P95:F2}ms Failure={FailurePercent:F2}%",
                bestStable.TargetShoppersPerSecond,
                bestStable.Snapshot.RequestsPerSecond,
                bestStable.Snapshot.P95Ms,
                bestStable.Assessment.FailurePercent);
            return;
        }

        logger.LogWarning("POS auto-ramp found no stable step with current thresholds.");
    }

    private async Task<StepOutcome> RunLoadStepAsync(Workload workload, string stepLabel, CancellationToken stoppingToken)
    {
        TokenBucketPacer? limiter = null;
        if (workload.TargetShoppersPerSecond > 0)
        {
            limiter = new TokenBucketPacer(
                workload.TargetShoppersPerSecond,
                burstRequests: Math.Max(workload.TargetShoppersPerSecond, workload.Concurrency * 4));
        }

        var stats = new LoadStats(workload.LatencySampleSize);
        var signals = new StepSignals();
        var started = Stopwatch.StartNew();
        var deadline = DateTimeOffset.UtcNow.Add(workload.Duration);

        using var logCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var logTask = LogLoopAsync(stats, started, workload.LogEvery, stepLabel, signals, logCts.Token);

        var workers = new Task[workload.Concurrency];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = WorkerLoopAsync(workerId: i, workload, stats, limiter, signals, deadline, stoppingToken);

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (breakerState?.IsOpen is true)
                signals.MarkBreakerOpen();

            logCts.Cancel();
            try { await logTask.ConfigureAwait(false); } catch { }
            if (limiter is not null)
                await limiter.DisposeAsync().ConfigureAwait(false);
        }

        var summary = stats.Snapshot(started.Elapsed);
        var assessment = AssessStep(workload, summary, signals.BreakerOpened);
        return new StepOutcome(workload.TargetShoppersPerSecond, summary, assessment);
    }

    private void LogStepCompletion(StepOutcome outcome, bool isRampStep)
    {
        var scope = isRampStep ? "POS ramp step complete." : "POS load complete.";
        logger.LogInformation(
            "{Scope} Target={Target}/s Elapsed={Elapsed} Requests={Requests} Rate={Rate:F0}/s Cache={Cache} Database={Database} None={None} Failures={Failures} FailurePct={FailurePct:F2}% P50={P50:F2}ms P95={P95:F2}ms P99={P99:F2}ms Stable={Stable} Reason={Reason}",
            scope,
            outcome.TargetShoppersPerSecond,
            outcome.Snapshot.Elapsed,
            outcome.Snapshot.Requests,
            outcome.Snapshot.RequestsPerSecond,
            outcome.Snapshot.CacheResponses,
            outcome.Snapshot.DatabaseResponses,
            outcome.Snapshot.NoneResponses,
            outcome.Snapshot.Failures,
            outcome.Assessment.FailurePercent,
            outcome.Snapshot.P50Ms,
            outcome.Snapshot.P95Ms,
            outcome.Snapshot.P99Ms,
            outcome.Assessment.IsStable,
            outcome.Assessment.Reason);
    }

    private async Task LogLoopAsync(LoadStats stats, Stopwatch started, TimeSpan period, string stepLabel, StepSignals signals, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (breakerState?.IsOpen is true)
                signals.MarkBreakerOpen();

            var snapshot = stats.Snapshot(started.Elapsed);
            logger.LogInformation(
                "POS load progress [{Step}] Elapsed={Elapsed} Requests={Requests} Rate={Rate:F0}/s Cache={Cache} Database={Database} None={None} Failures={Failures} P95={P95:F2}ms BreakerOpen={BreakerOpen}",
                stepLabel,
                snapshot.Elapsed,
                snapshot.Requests,
                snapshot.RequestsPerSecond,
                snapshot.CacheResponses,
                snapshot.DatabaseResponses,
                snapshot.NoneResponses,
                snapshot.Failures,
                snapshot.P95Ms,
                signals.BreakerOpened);
        }
    }

    private async Task WorkerLoopAsync(
        int workerId,
        Workload workload,
        LoadStats stats,
        TokenBucketPacer? limiter,
        StepSignals signals,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var rng = new Random(unchecked(Environment.TickCount + (workerId * 7919)));

        while (!ct.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                return;

            if (breakerState?.IsOpen is true)
                signals.MarkBreakerOpen();

            if (limiter is not null)
                await limiter.WaitAsync(ct).ConfigureAwait(false);

            var query = NextQuery(rng, workload);
            try
            {
                var result = await searchService.SearchAsync(query, ct).ConfigureAwait(false);
                stats.Record(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (breakerState?.IsOpen is true)
                    signals.MarkBreakerOpen();
                stats.RecordFailure();
            }
        }
    }

    private static StepAssessment AssessStep(Workload workload, LoadSnapshot snapshot, bool breakerOpened)
    {
        var attempts = snapshot.Requests + snapshot.Failures;
        var failurePercent = attempts > 0 ? (snapshot.Failures * 100d) / attempts : 100d;

        var stable = snapshot.Requests > 0 &&
                     failurePercent <= workload.MaxFailurePercent &&
                     (workload.MaxP95Ms <= 0d || snapshot.P95Ms <= workload.MaxP95Ms) &&
                     (!workload.TreatOpenCircuitAsUnstable || !breakerOpened);

        if (stable)
            return new StepAssessment(true, failurePercent, breakerOpened, "met-stability-thresholds");

        var reasons = new List<string>(3);
        if (snapshot.Requests == 0)
            reasons.Add("no-successful-requests");
        if (failurePercent > workload.MaxFailurePercent)
            reasons.Add(FormattableString.Invariant($"failure-percent>{workload.MaxFailurePercent:0.##}%"));
        if (workload.MaxP95Ms > 0d && snapshot.P95Ms > workload.MaxP95Ms)
            reasons.Add(FormattableString.Invariant($"p95>{workload.MaxP95Ms:0.##}ms"));
        if (workload.TreatOpenCircuitAsUnstable && breakerOpened)
            reasons.Add("breaker-opened");

        return new StepAssessment(
            IsStable: false,
            FailurePercent: failurePercent,
            BreakerOpened: breakerOpened,
            Reason: reasons.Count == 0 ? "unstable" : string.Join(',', reasons));
    }

    internal static int[] ParseRampSteps(string rampSteps, int fallbackTargetSps)
    {
        static int NormalizeFallback(int configuredTarget)
            => configuredTarget > 0 ? configuredTarget : 2200;

        var fallback = NormalizeFallback(fallbackTargetSps);
        if (string.IsNullOrWhiteSpace(rampSteps))
            return [fallback];

        var unique = new HashSet<int>();
        var parsed = new List<int>();

        var tokens = rampSteps.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;

            if (value <= 0 || !unique.Add(value))
                continue;

            parsed.Add(value);
        }

        return parsed.Count == 0 ? [fallback] : [.. parsed];
    }

    private static string NextQuery(Random rng, Workload workload)
    {
        var roll = rng.Next(100);
        if (roll < workload.HotQueryPercent)
            return workload.HotQuery;

        var cashierUpper = workload.HotQueryPercent + workload.CashierQueryPercent;
        if (roll < cashierUpper)
            return workload.CashierQuery;

        var upcUpper = cashierUpper + workload.LookupUpcPercent;
        if (roll < upcUpper)
            return workload.LookupUpcQuery;

        if (workload.MaxRandomProductNumber < 6)
            return workload.HotQuery;

        var number = rng.Next(6, workload.MaxRandomProductNumber + 1);
        return $"code:PRD-{number:D6}";
    }

    private static Workload Normalize(PosSearchLoadOptions load, PosSearchDemoOptions demo)
    {
        var hotPercent = Math.Clamp(load.HotQueryPercent, 0, 100);
        var cashierPercent = Math.Clamp(load.CashierQueryPercent, 0, 100 - hotPercent);
        var lookupUpcPercent = Math.Clamp(load.LookupUpcPercent, 0, 100 - hotPercent - cashierPercent);
        var randomPercent = 100 - hotPercent - cashierPercent - lookupUpcPercent;

        return new Workload(
            StopHostOnCompletion: load.StopHostOnCompletion,
            Duration: load.Duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : load.Duration,
            Concurrency: Math.Max(1, load.Concurrency),
            LogEvery: load.LogEvery <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : load.LogEvery,
            TargetShoppersPerSecond: Math.Max(0, load.TargetShoppersPerSecond),
            EnableAutoRamp: load.EnableAutoRamp,
            RampSteps: load.RampSteps ?? string.Empty,
            RampStepDuration: load.RampStepDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(20) : load.RampStepDuration,
            StopOnFirstUnstable: load.StopOnFirstUnstable,
            TreatOpenCircuitAsUnstable: load.TreatOpenCircuitAsUnstable,
            MaxFailurePercent: Math.Clamp(load.MaxFailurePercent, 0d, 100d),
            MaxP95Ms: Math.Max(0d, load.MaxP95Ms),
            HotQuery: string.IsNullOrWhiteSpace(load.HotQuery) ? $"code:{demo.LookupCode}" : load.HotQuery.Trim(),
            CashierQuery: string.IsNullOrWhiteSpace(demo.CashierQuery) ? "pencil" : demo.CashierQuery.Trim(),
            LookupUpcQuery: string.IsNullOrWhiteSpace(demo.LookupUpc) ? "upc:012345678901" : $"upc:{demo.LookupUpc.Trim()}",
            HotQueryPercent: hotPercent,
            CashierQueryPercent: cashierPercent,
            LookupUpcPercent: lookupUpcPercent,
            RandomQueryPercent: randomPercent,
            MaxRandomProductNumber: Math.Max(6, demo.SeedProductCount),
            LatencySampleSize: Math.Clamp(load.LatencySampleSize, 256, 1 << 20));
    }

    private sealed class LoadStats(int latencySampleSize)
    {
        private readonly long[] _latencyMicros = new long[RoundUpToPowerOfTwo(latencySampleSize)];

        private long _requests;
        private long _cacheResponses;
        private long _databaseResponses;
        private long _noneResponses;
        private long _failures;
        private int _latencyCursor;

        public void Record(PosSearchResult result)
        {
            Interlocked.Increment(ref _requests);

            switch (result.Source)
            {
                case PosSearchSource.Cache:
                    Interlocked.Increment(ref _cacheResponses);
                    break;
                case PosSearchSource.Database:
                    Interlocked.Increment(ref _databaseResponses);
                    break;
                default:
                    Interlocked.Increment(ref _noneResponses);
                    break;
            }

            var micros = Math.Max(0L, (long)(result.Elapsed.TotalMilliseconds * 1000d));
            var slot = Interlocked.Increment(ref _latencyCursor) - 1;
            _latencyMicros[slot & (_latencyMicros.Length - 1)] = micros;
        }

        public void RecordFailure() => Interlocked.Increment(ref _failures);

        public LoadSnapshot Snapshot(TimeSpan elapsed)
        {
            var requests = Volatile.Read(ref _requests);
            var cache = Volatile.Read(ref _cacheResponses);
            var database = Volatile.Read(ref _databaseResponses);
            var none = Volatile.Read(ref _noneResponses);
            var failures = Volatile.Read(ref _failures);
            var elapsedSeconds = Math.Max(0.001d, elapsed.TotalSeconds);
            var rate = requests / elapsedSeconds;
            var (p50, p95, p99) = ComputePercentilesMs();

            return new LoadSnapshot(
                Elapsed: elapsed,
                Requests: requests,
                CacheResponses: cache,
                DatabaseResponses: database,
                NoneResponses: none,
                Failures: failures,
                RequestsPerSecond: rate,
                P50Ms: p50,
                P95Ms: p95,
                P99Ms: p99);
        }

        private (double P50Ms, double P95Ms, double P99Ms) ComputePercentilesMs()
        {
            var samples = _latencyMicros;
            var scratch = new long[samples.Length];
            Array.Copy(samples, scratch, samples.Length);

            var count = 0;
            for (var i = 0; i < scratch.Length; i++)
            {
                if (scratch[i] > 0)
                    scratch[count++] = scratch[i];
            }

            if (count == 0)
                return (0, 0, 0);

            Array.Sort(scratch, 0, count);

            return (
                ToMs(Percentile(scratch, count, 0.50)),
                ToMs(Percentile(scratch, count, 0.95)),
                ToMs(Percentile(scratch, count, 0.99)));
        }

        private static long Percentile(long[] values, int count, double percentile)
        {
            if (count == 0)
                return 0;

            var index = (int)Math.Ceiling((count - 1) * percentile);
            index = Math.Clamp(index, 0, count - 1);
            return values[index];
        }

        private static double ToMs(long micros) => micros / 1000d;

        private static int RoundUpToPowerOfTwo(int value)
        {
            var clamped = Math.Clamp(value, 2, 1 << 20);
            var n = 1;
            while (n < clamped)
                n <<= 1;
            return n;
        }
    }

    private readonly record struct Workload(
        bool StopHostOnCompletion,
        TimeSpan Duration,
        int Concurrency,
        TimeSpan LogEvery,
        int TargetShoppersPerSecond,
        bool EnableAutoRamp,
        string RampSteps,
        TimeSpan RampStepDuration,
        bool StopOnFirstUnstable,
        bool TreatOpenCircuitAsUnstable,
        double MaxFailurePercent,
        double MaxP95Ms,
        string HotQuery,
        string CashierQuery,
        string LookupUpcQuery,
        int HotQueryPercent,
        int CashierQueryPercent,
        int LookupUpcPercent,
        int RandomQueryPercent,
        int MaxRandomProductNumber,
        int LatencySampleSize);

    private readonly record struct LoadSnapshot(
        TimeSpan Elapsed,
        long Requests,
        long CacheResponses,
        long DatabaseResponses,
        long NoneResponses,
        long Failures,
        double RequestsPerSecond,
        double P50Ms,
        double P95Ms,
        double P99Ms);

    private readonly record struct StepOutcome(
        int TargetShoppersPerSecond,
        LoadSnapshot Snapshot,
        StepAssessment Assessment);

    private readonly record struct StepAssessment(
        bool IsStable,
        double FailurePercent,
        bool BreakerOpened,
        string Reason);

    private sealed class StepSignals
    {
        private int _breakerOpened;

        public bool BreakerOpened => Volatile.Read(ref _breakerOpened) == 1;

        public void MarkBreakerOpen() => Interlocked.Exchange(ref _breakerOpened, 1);
    }
}
