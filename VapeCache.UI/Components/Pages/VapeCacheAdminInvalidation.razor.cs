using VapeCache.Abstractions.Caching;
using VapeCache.UI.Features.Admin;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin invalidation page code-behind.
/// </summary>
public partial class VapeCacheAdminInvalidation
{
    private readonly VapeCacheAdminOrchestrator _admin;

    private string _tag = string.Empty;
    private string _zone = string.Empty;
    private string _key = string.Empty;
    private string? _message;
    private string? _error;
    private IReadOnlyList<CacheIntentEntry> _recentPolicies = Array.Empty<CacheIntentEntry>();

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminInvalidation"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminInvalidation(VapeCacheAdminOrchestrator admin)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    /// <summary>
    /// Executes component initialization.
    /// </summary>
    protected override Task OnInitializedAsync()
        => RefreshAsync();

    private Task RefreshAsync()
    {
        try
        {
            _recentPolicies = _admin.GetRecentPolicies(100);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        return Task.CompletedTask;
    }

    private async Task InvalidateTagAsync()
    {
        try
        {
            var version = await _admin.InvalidateTagAsync(_tag).ConfigureAwait(false);
            _message = $"Tag '{_tag.Trim()}' invalidated. New version={version}.";
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task InvalidateZoneAsync()
    {
        try
        {
            var version = await _admin.InvalidateZoneAsync(_zone).ConfigureAwait(false);
            _message = $"Zone '{_zone.Trim()}' invalidated. New version={version}.";
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task InvalidateKeyAsync()
    {
        try
        {
            var removed = await _admin.InvalidateKeyAsync(_key).ConfigureAwait(false);
            _message = removed
                ? $"Key '{_key.Trim()}' removed."
                : $"Key '{_key.Trim()}' not found.";
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }
}
