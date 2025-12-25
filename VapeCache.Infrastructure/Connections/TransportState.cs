namespace VapeCache.Infrastructure.Connections;

internal enum TransportState
{
    Created = 0,
    Connecting = 1,
    Ready = 2,
    Faulted = 3,
    Disposed = 4,
}
