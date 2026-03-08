namespace VapeCache.Abstractions.Diagnostics;

/// <summary>
/// Defines the cache backend state contract.
/// </summary>
public interface ICacheBackendState
{
    /// <summary>
    /// Gets the effective backend.
    /// </summary>
    BackendType EffectiveBackend { get; }
}
