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
    double HitRate);
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

## 7) Recommended Charting

For chart visuals, bind `_history` into:

- `ApexCharts.Blazor`
- `Plotly.Blazor`
- `ChartJs.Blazor`

Map these Y-series at minimum:

- `HitRate`
- `Hits` / `Misses` deltas
- `FallbackToMemory`
- `RedisBreakerOpened`
- `StampedeKeyRejected`

## 8) Operational Notes

- Keep `/vapecache/stream` behind auth in production.
- Use bounded history in UI to avoid browser memory growth.
- Treat `/status` as startup fallback if stream is unavailable.
- Correlate with OTel metrics for large keys and evictions:
  - `cache.set.payload.bytes`
  - `cache.set.large_key`
  - `cache.evictions`
