namespace VapeCache.Infrastructure.Connections;

internal readonly record struct PendingRequest(
    ReadOnlyMemory<byte> Command,
    PendingOperation Op,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte>[]? Payloads,
    int PayloadCount,
    bool AppendCrlf,
    bool AppendCrlfPerPayload,
    byte[]? HeaderBuffer,
    ReadOnlyMemory<byte>[]? PayloadArrayBuffer)
{
    public PendingRequest(ReadOnlyMemory<byte> command, PendingOperation op)
        : this(command, op, ReadOnlyMemory<byte>.Empty, null, 0, false, true, null, null) { }
}
