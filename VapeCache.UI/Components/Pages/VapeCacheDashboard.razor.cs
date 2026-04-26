using VapeCache.Abstractions.Connections;
using VapeCache.UI.Features.Dashboard;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Represents the vape cache dashboard.
/// </summary>
public partial class VapeCacheDashboard : IAsyncDisposable
{
    private static readonly IComparer<RedisMuxLaneSnapshot> LaneIndexComparer =
        Comparer<RedisMuxLaneSnapshot>.Create(static (x, y) => x.LaneIndex.CompareTo(y.LaneIndex));
    private readonly VapeCacheDashboardOrchestrator _dashboard;

    private VapeCacheDashboardSnapshot _snapshot = VapeCacheDashboardSnapshot.Empty;
    private RedisMuxLaneSnapshot[] _sortedLanes = Array.Empty<RedisMuxLaneSnapshot>();
    private LaneSummary[] _laneSummaries = Array.Empty<LaneSummary>();
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshLoop;
    private bool _disposed;

    /// <summary>
    /// Executes vape cache dashboard.
    /// </summary>
    public VapeCacheDashboard(VapeCacheDashboardOrchestrator dashboard)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
    }

    /// <summary>
    /// Executes on initialized async.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync().ConfigureAwait(false);

        _refreshCts = new CancellationTokenSource();
        _refreshLoop = RunRefreshLoopAsync(_refreshCts.Token);
    }

    private Task RefreshNowAsync()
        => RefreshAsync();

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        try
        {
            while (!ct.IsCancellationRequested &&
                   await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal.
        }
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed)
            return;

        var snapshot = await _dashboard.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (ReferenceEquals(snapshot, _snapshot))
            return;

        _snapshot = snapshot;
        UpdateSortedLanes(_snapshot.Lanes);
        await InvokeAsync(StateHasChanged);
    }

    private void UpdateSortedLanes(IReadOnlyList<RedisMuxLaneSnapshot> lanes)
    {
        if (lanes.Count == 0)
        {
            _sortedLanes = Array.Empty<RedisMuxLaneSnapshot>();
            _laneSummaries = Array.Empty<LaneSummary>();
            return;
        }

        if (_sortedLanes.Length != lanes.Count)
            _sortedLanes = new RedisMuxLaneSnapshot[lanes.Count];

        for (var i = 0; i < lanes.Count; i++)
            _sortedLanes[i] = lanes[i];

        Array.Sort(_sortedLanes, LaneIndexComparer);
        _laneSummaries = BuildLaneSummaries(_sortedLanes);
    }

    private string CurrentServingPathLabel
        => _snapshot.BreakerForcedOpen || _snapshot.BreakerOpen
            ? "InMemory Fallback"
            : "Redis Primary";

    private string CurrentServingPathBadgeClass
        => _snapshot.BreakerForcedOpen || _snapshot.BreakerOpen
            ? "panel-badge-danger"
            : "panel-badge-ok";

    private string CurrentServingPathKickerClass
        => _snapshot.BreakerForcedOpen || _snapshot.BreakerOpen
            ? "dashboard-kicker-danger"
            : "dashboard-kicker-ok";

    private static LaneSummary[] BuildLaneSummaries(IReadOnlyList<RedisMuxLaneSnapshot> lanes)
    {
        var order = new[]
        {
            ("read-write", "Fast Lanes"),
            ("bulk-read-write", "Bulk Lanes"),
            ("pubsub-read-write", "Pub/Sub Lanes"),
            ("blocking-read-write", "Blocking Lanes")
        };

        var summaries = new List<LaneSummary>(order.Length + 1);
        long totalOperations = 0;
        var totalHealthy = 0;

        for (var i = 0; i < lanes.Count; i++)
        {
            totalOperations += lanes[i].Operations;
            if (lanes[i].Healthy)
                totalHealthy++;
        }

        summaries.Add(new LaneSummary("All Lanes", lanes.Count, totalHealthy, totalOperations));

        for (var i = 0; i < order.Length; i++)
        {
            var role = order[i].Item1;
            var label = order[i].Item2;
            var count = 0;
            var healthy = 0;
            long operations = 0;

            for (var laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
            {
                var lane = lanes[laneIndex];
                if (!string.Equals(lane.Role, role, StringComparison.Ordinal))
                    continue;

                count++;
                operations += lane.Operations;
                if (lane.Healthy)
                    healthy++;
            }

            if (count > 0)
                summaries.Add(new LaneSummary(label, count, healthy, operations));
        }

        return FinalizeLaneSummaries(summaries);
    }

    private static LaneSummary[] FinalizeLaneSummaries(List<LaneSummary> summaries)
    {
        if (summaries.Count == 0)
            return Array.Empty<LaneSummary>();

        var total = summaries[0];
        var totalLanes = Math.Max(1, total.Count);
        var totalHealthy = Math.Max(1, total.HealthyCount);
        var totalOperations = Math.Max(1L, total.TotalOperations);
        var finalized = new LaneSummary[summaries.Count];

        for (var i = 0; i < summaries.Count; i++)
        {
            var current = summaries[i];
            finalized[i] = current with
            {
                LaneShareText = i == 0 ? "100%" : $"{(current.Count * 100d / totalLanes):F1}%",
                HealthyShareText = i == 0 ? "100%" : $"{(current.HealthyCount * 100d / totalHealthy):F1}%",
                OperationsShareText = i == 0 ? "100%" : $"{(current.TotalOperations * 100d / totalOperations):F1}%"
            };
        }

        return finalized;
    }

    private readonly record struct LaneSummary(
        string Label,
        int Count,
        int HealthyCount,
        long TotalOperations,
        string LaneShareText = "0%",
        string HealthyShareText = "0%",
        string OperationsShareText = "0%");

    /// <summary>
    /// Executes dispose async.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        var cts = _refreshCts;
        _refreshCts = null;
        if (cts is not null)
        {
            cts.Cancel();
        }

        var refreshLoop = _refreshLoop;
        _refreshLoop = null;
        if (refreshLoop is not null)
        {
            try
            {
                await refreshLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when refresh loop is canceled during disposal.
            }
        }

        cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
