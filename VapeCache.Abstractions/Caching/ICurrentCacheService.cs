namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the current cache service contract.
/// </summary>
public interface ICurrentCacheService
{
    /// <summary>
    /// Gets the current name.
    /// </summary>
    string CurrentName { get; }
    /// <summary>
    /// Executes set current.
    /// </summary>
    void SetCurrent(string name);
}
