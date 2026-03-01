# Blazor Realtime Dashboard Example

This example shows how to consume VapeCache operational feeds from Blazor using:

- `GET /vapecache/stream` (SSE realtime feed)
- `GET /vapecache/status` (snapshot/fallback)

The goal is a clean, production-ready integration path developers can copy directly.

The guidance below aligns with current Blazor Web App render-mode APIs:

- `builder.Services.AddRazorComponents().AddInteractiveServerComponents()`
- `app.MapRazorComponents<App>().AddInteractiveServerRenderMode()`
- `@rendermode InteractiveServer` where needed

## 1) Host Wiring (API)

In your API host (`Program.cs`), enable VapeCache endpoints:

```csharp
builder.AddVapeCache()
    .WithRedisFromAspire("redis")
    .WithHealthChecks()
    .WithAspireTelemetry()
    .WithAutoMappedEndpoints(options =>
    {
        options.Prefix = "/vapecache";
        options.EnableLiveStream = true;
        options.LiveSampleInterval = TimeSpan.FromMilliseconds(500);
        options.LiveChannelCapacity = 512;
    });

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
```

This maps:

- `/vapecache/status`
- `/vapecache/stats`
- `/vapecache/stream`

## 2) Shared DTO

Create a DTO in your Blazor app that matches the stream payload:

```csharp
public sealed record RedisMuxLaneSnapshot(
    int LaneIndex,
    int ConnectionId,
    string Role,
    int WriteQueueDepth,
    int InFlight,
    int MaxInFlight,
    double InFlightUtilization,
    long BytesSent,
    long BytesReceived,
    long Operations,
    long Failures,
    bool Healthy);

public sealed record VapeCacheLiveSample(
    DateTimeOffset TimestampUtc,
    string CurrentBackend,
    long Hits,
    long Misses,
    long SetCalls,
    long RemoveCalls,
    long FallbackToMemory,
    long RedisBreakerOpened,
    long StampedeKeyRejected,
    long StampedeLockWaitTimeout,
    long StampedeFailureBackoffRejected,
    double HitRate,
    IReadOnlyList<RedisMuxLaneSnapshot>? Lanes);
```

## 3) Blazor Web App Program.cs (Current Pattern)

In a Blazor Web App host, enable interactive server rendering and map root components:

```csharp
using YourApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

For interactive WASM or Auto modes, use the equivalent `AddInteractiveWebAssemblyComponents` / `AddInteractiveWebAssemblyRenderMode` configuration.

### InteractiveAuto Variant

If you want Blazor Web App `InteractiveAuto`, wire both server and WebAssembly interactive services:

```csharp
using YourApp.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode();

app.Run();
```

Then change the page directive in the component from:

```razor
@rendermode InteractiveServer
```

to:

```razor
@rendermode InteractiveAuto
```
## 4) Stream Client Service (Blazor Server/Web App)

Create `Services/VapeCacheStreamClient.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;

public sealed class VapeCacheStreamClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<VapeCacheLiveSample> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/vapecache/stream");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // SSE frame line format: "data: {json}"
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
                continue;

            var sample = JsonSerializer.Deserialize<VapeCacheLiveSample>(payload, JsonOptions);
            if (sample is not null)
                yield return sample;
        }
    }
}
```

## 5) Blazor DI Wiring

In Blazor `Program.cs`:

```csharp
builder.Services.AddHttpClient<VapeCacheStreamClient>(client =>
{
    // If same host app, this can be relative with NavigationManager base URI via a custom handler.
    // For split frontend/backend, set the API base URL explicitly.
    client.BaseAddress = new Uri("https://localhost:7111");
});
```

## 6) Realtime Component

Create `Components/Pages/VapeCacheDashboard.razor`:

```razor
@page "/vapecache-dashboard"
@rendermode InteractiveServer
@implements IAsyncDisposable
@inject VapeCacheStreamClient StreamClient

<h3>VapeCache Live Dashboard</h3>

<div class="stats-grid">
    <div class="card">
        <h4>Backend</h4>
        <p>@_latest?.CurrentBackend</p>
    </div>
    <div class="card">
        <h4>Hit Rate</h4>
        <p>@((_latest?.HitRate ?? 0).ToString("P2"))</p>
    </div>
    <div class="card">
        <h4>Hits / Misses</h4>
        <p>@_latest?.Hits / @_latest?.Misses</p>
    </div>
    <div class="card">
        <h4>Large-Key Risk Signals</h4>
        <p>Use metrics: <code>cache.set.payload.bytes</code>, <code>cache.set.large_key</code></p>
    </div>
    <div class="card">
        <h4>Eviction Signals</h4>
        <p>Use metric: <code>cache.evictions</code> (reason tag)</p>
    </div>
    <div class="card">
        <h4>Stampede Protection</h4>
        <p>Rejected: @_latest?.StampedeKeyRejected</p>
        <p>Lock timeout: @_latest?.StampedeLockWaitTimeout</p>
        <p>Backoff reject: @_latest?.StampedeFailureBackoffRejected</p>
    </div>
</div>

<h4>Recent Samples (@_history.Count)</h4>
<table class="table">
    <thead>
        <tr>
            <th>UTC</th>
            <th>HitRate</th>
            <th>Hits</th>
            <th>Misses</th>
            <th>Fallbacks</th>
            <th>Breaker Opens</th>
        </tr>
    </thead>
    <tbody>
        @foreach (var sample in _history)
        {
            <tr>
                <td>@sample.TimestampUtc.ToString("HH:mm:ss.fff")</td>
                <td>@sample.HitRate.ToString("P1")</td>
                <td>@sample.Hits</td>
                <td>@sample.Misses</td>
                <td>@sample.FallbackToMemory</td>
                <td>@sample.RedisBreakerOpened</td>
            </tr>
        }
    </tbody>
</table>

@code {
    private readonly List<VapeCacheLiveSample> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private VapeCacheLiveSample? _latest;
    private const int MaxPoints = 120;

    protected override void OnInitialized()
    {
        _cts = new CancellationTokenSource();
        _readerTask = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sample in StreamClient.StreamAsync(ct))
            {
                _latest = sample;
                _history.Add(sample);
                AddLaneSample(sample);
                if (_history.Count > MaxPoints)
                    _history.RemoveAt(0);

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        if (_readerTask is not null)
            await _readerTask.ConfigureAwait(false);
    }
}
```

## 7) Oscilloscope Lane Graph (Old-School Style)

Use `sample.Lanes` as cumulative counters and chart per-sample deltas for a stable waveform.
Inside your pump loop, call `AddLaneSample(sample)` before `StateHasChanged`.

```razor
<div class="scope-shell">
    <div class="scope-title">MUX LANE OSCILLOSCOPE (KB/TICK)</div>
    <svg class="scope-screen" viewBox="0 0 1200 260" preserveAspectRatio="none">
        @foreach (var wave in _laneWaves)
        {
            <polyline class="scope-trace"
                      style="stroke:@GetLaneColor(wave.Key);"
                      points="@BuildPolyline(wave.Value)" />
        }
    </svg>
</div>

@code {
    private readonly Dictionary<int, List<double>> _laneWaves = new();
    private readonly Dictionary<int, long> _laneLastBytes = new();
    private const int ScopePoints = 120;

    private void AddLaneSample(VapeCacheLiveSample sample)
    {
        foreach (var lane in sample.Lanes ?? Array.Empty<RedisMuxLaneSnapshot>())
        {
            var totalBytes = lane.BytesSent + lane.BytesReceived;
            _laneLastBytes.TryGetValue(lane.ConnectionId, out var previousBytes);
            var deltaBytes = Math.Max(0L, totalBytes - previousBytes);
            _laneLastBytes[lane.ConnectionId] = totalBytes;

            var kbPerTick = deltaBytes / 1024d;
            if (!_laneWaves.TryGetValue(lane.ConnectionId, out var wave))
            {
                wave = new List<double>(ScopePoints);
                _laneWaves[lane.ConnectionId] = wave;
            }

            wave.Add(Math.Min(100d, kbPerTick));
            if (wave.Count > ScopePoints)
                wave.RemoveAt(0);
        }
    }

    private static string BuildPolyline(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder(values.Count * 14);
        var xStep = values.Count == 1 ? 0d : 1180d / (values.Count - 1);

        for (var i = 0; i < values.Count; i++)
        {
            var x = 10d + (i * xStep);
            var y = 240d - ((values[i] / 100d) * 220d);
            if (i > 0)
                sb.Append(' ');

            sb.Append(x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string GetLaneColor(int connectionId)
        => (connectionId % 4) switch
        {
            0 => "#8CFF9E",
            1 => "#66E2FF",
            2 => "#FFE16E",
            _ => "#FF8A6E"
        };
}
```

```css
.scope-shell {
    border: 1px solid #2a4f2f;
    border-radius: 8px;
    padding: 10px;
    background: radial-gradient(circle at 30% 20%, #0b1f11 0%, #050b08 70%);
    box-shadow: inset 0 0 16px rgba(90, 255, 120, 0.12);
}

.scope-title {
    color: #90ffa8;
    font-family: "Consolas", "Courier New", monospace;
    font-size: 0.75rem;
    letter-spacing: 0.08em;
    margin-bottom: 8px;
}

.scope-screen {
    width: 100%;
    height: 260px;
    background:
        linear-gradient(rgba(130, 255, 150, 0.08) 1px, transparent 1px) 0 0 / 100% 20px,
        linear-gradient(90deg, rgba(130, 255, 150, 0.08) 1px, transparent 1px) 0 0 / 30px 100%,
        #050b08;
}

.scope-trace {
    fill: none;
    stroke-width: 2;
    stroke-linejoin: round;
    stroke-linecap: round;
    filter: drop-shadow(0 0 4px currentColor);
    animation: scope-flicker 180ms steps(2) infinite;
}

@keyframes scope-flicker {
    0%, 100% { opacity: 0.9; }
    50% { opacity: 0.75; }
}
```

## 8) Operational Notes

- Keep `/vapecache/stream` behind auth in production.
- Use bounded history in UI to avoid browser memory growth.
- Treat `/status` as startup fallback if stream is unavailable.
- Correlate with OTel metrics for large keys and evictions:
  - `cache.set.payload.bytes`
  - `cache.set.large_key`
  - `cache.evictions`
