using VapeCache.Abstractions.Connections;
using VapeCache.UI.Features.Dashboard;

namespace VapeCache.UI.Components.Pages;

public partial class VapeCacheDashboard : IAsyncDisposable
{
    private static readonly IComparer<RedisMuxLaneSnapshot> LaneIndexComparer =
        Comparer<RedisMuxLaneSnapshot>.Create(static (x, y) => x.LaneIndex.CompareTo(y.LaneIndex));
    private readonly VapeCacheDashboardOrchestrator _dashboard;

    private VapeCacheDashboardSnapshot _snapshot = VapeCacheDashboardSnapshot.Empty;
    private RedisMuxLaneSnapshot[] _sortedLanes = Array.Empty<RedisMuxLaneSnapshot>();
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshLoop;
    private bool _disposed;

    public VapeCacheDashboard(VapeCacheDashboardOrchestrator dashboard)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
    }

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
            return;
        }

        if (_sortedLanes.Length != lanes.Count)
            _sortedLanes = new RedisMuxLaneSnapshot[lanes.Count];

        for (var i = 0; i < lanes.Count; i++)
            _sortedLanes[i] = lanes[i];

        Array.Sort(_sortedLanes, LaneIndexComparer);
    }

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
