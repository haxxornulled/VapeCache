namespace VapeCache.Infrastructure.Connections;

internal readonly struct PendingRequest
{
    public PendingRequest(
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
        this.Command = Command;
        this.Op = Op;
        this.Payload = Payload;
        this.Payloads = Payloads;
        this.PayloadCount = PayloadCount;
        this.AppendCrlf = AppendCrlf;
        this.AppendCrlfPerPayload = AppendCrlfPerPayload;
        this.HeaderBuffer = HeaderBuffer;
        this.PayloadArrayBuffer = PayloadArrayBuffer;
    }

    public PendingRequest(ReadOnlyMemory<byte> command, PendingOperation op)
        : this(command, op, ReadOnlyMemory<byte>.Empty, null, 0, false, true, null, null) { }

    public ReadOnlyMemory<byte> Command { get; }
    public PendingOperation Op { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public ReadOnlyMemory<byte>[]? Payloads { get; }
    public int PayloadCount { get; }
    public bool AppendCrlf { get; }
    public bool AppendCrlfPerPayload { get; }
    public byte[]? HeaderBuffer { get; }
    public ReadOnlyMemory<byte>[]? PayloadArrayBuffer { get; }
}
