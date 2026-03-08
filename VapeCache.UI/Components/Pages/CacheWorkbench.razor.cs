using VapeCache.UI.Features.CacheWorkbench;
using VapeCache.Abstractions.Diagnostics;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Represents the cache workbench.
/// </summary>
public partial class CacheWorkbench
{
    private readonly CacheWorkbenchOrchestrator _orchestrator;

    private string _key = "demo:welcome";
    private string _value = "Hello from VapeCache.UI";
    private string _breakerReason = "manual-ui-force-open";
    private int _ttlSeconds = 120;
    private string? _message;
    private CacheWorkbenchStatus _status = new(
        Backend: BackendType.Redis,
        BreakerOpen: false,
        ConsecutiveFailures: 0,
        BreakerOpenRemaining: null,
        BreakerForcedOpen: false,
        BreakerReason: null,
        GetCalls: 0,
        Hits: 0,
        Misses: 0,
        SetCalls: 0,
        RemoveCalls: 0,
        FallbackToMemory: 0);

    /// <summary>
    /// Executes cache workbench.
    /// </summary>
    public CacheWorkbench(CacheWorkbenchOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    /// <summary>
    /// Executes on initialized async.
    /// </summary>
    protected override Task OnInitializedAsync()
        => RefreshAsync();

    private async Task SetAsync()
    {
        try
        {
            var result = await _orchestrator.SetStringAsync(_key, _value, _ttlSeconds);
            _message = $"Set '{result.Key}' ({result.Value.Length} chars).";
        }
        catch (Exception ex)
        {
            _message = $"Set failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task GetAsync()
    {
        try
        {
            var result = await _orchestrator.ReadStringAsync(_key);
            _message = result.Found
                ? $"Read '{result.Key}': {result.Value}"
                : $"Key '{result.Key}' was not found.";
        }
        catch (Exception ex)
        {
            _message = $"Get failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task RemoveAsync()
    {
        try
        {
            var removed = await _orchestrator.RemoveAsync(_key);
            _message = removed
                ? $"Removed '{_key}'."
                : $"Nothing removed for '{_key}'.";
        }
        catch (Exception ex)
        {
            _message = $"Remove failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task ForceOpenBreakerAsync()
    {
        try
        {
            await _orchestrator.ForceBreakerOpenAsync(_breakerReason);
            _message = "Breaker forced open.";
        }
        catch (Exception ex)
        {
            _message = $"Force-open failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task ClearForcedOpenBreakerAsync()
    {
        try
        {
            await _orchestrator.ClearBreakerForceOpenAsync();
            _message = "Breaker forced-open flag cleared.";
        }
        catch (Exception ex)
        {
            _message = $"Clear failed: {ex.Message}";
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _status = await _orchestrator.GetStatusAsync();
    }
}
