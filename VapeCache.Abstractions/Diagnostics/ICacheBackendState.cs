namespace VapeCache.Abstractions.Diagnostics;

public interface ICacheBackendState
{
    BackendType EffectiveBackend { get; }
}
