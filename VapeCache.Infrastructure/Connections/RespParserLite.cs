using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace VapeCache.Infrastructure.Connections;

/// <summary>
/// Allocation-free RESP parser for perf gates and microbenchmarks.
/// Returns slices into the provided buffer; caller owns buffer lifetime.
/// </summary>
internal static class RespParserLite
{
    internal readonly struct RespValueLite
    {
        public RespValueLite(RedisRespReader.RespKind kind, ReadOnlyMemory<byte> data = default, long integer = 0, int arrayLength = 0)
        {
            Kind = kind;
            Data = data;
            Integer = integer;
            ArrayLength = arrayLength;
        }

        public RedisRespReader.RespKind Kind { get; }
        public ReadOnlyMemory<byte> Data { get; }
        public long Integer { get; }
        public int ArrayLength { get; }
    }

    public static bool TryParse(ReadOnlyMemory<byte> buffer, out int consumed, out RespValueLite value)
    {
        var span = buffer.Span;
        consumed = 0;
        value = default;
        if (span.IsEmpty)
            return false;

        switch (span[0])
        {
            case (byte)'+': // simple string
            case (byte)'-': // error
            {
                if (!TryReadLine(span.Slice(1), out var lineConsumed, out var line))
                    return false;
                consumed = 1 + lineConsumed;
                value = new RespValueLite(span[0] == (byte)'+' ? RedisRespReader.RespKind.SimpleString : RedisRespReader.RespKind.Error, buffer.Slice(1, line.Length));
                return true;
            }
            case (byte)':': // integer
            {
                if (!TryReadLine(span.Slice(1), out var lineConsumed, out var line))
                    return false;
                consumed = 1 + lineConsumed;
                value = new RespValueLite(RedisRespReader.RespKind.Integer, default, ParseInt64(line));
                return true;
            }
            case (byte)'$': // bulk string
            {
                if (!TryReadLine(span.Slice(1), out var lineConsumed, out var line))
                    return false;
                if (!TryParseLength(line, out var len))
                    throw new InvalidOperationException($"Invalid bulk length: {line.ToString()}");

                var headerLen = 1 + lineConsumed;
                if (len == -1)
                {
                    consumed = headerLen;
                    value = new RespValueLite(RedisRespReader.RespKind.NullBulkString);
                    return true;
                }

                var total = headerLen + len + 2;
                if (span.Length < total)
                    return false;

                value = new RespValueLite(RedisRespReader.RespKind.BulkString, buffer.Slice(headerLen, len));
                consumed = total;
                return true;
            }
            case (byte)'*': // array
            {
                if (!TryReadLine(span.Slice(1), out var lineConsumed, out var line))
                    return false;
                if (!TryParseLength(line, out var len))
                    throw new InvalidOperationException($"Invalid array length: {line.ToString()}");

                var headerLen = 1 + lineConsumed;
                if (span.Length < headerLen)
                    return false;

                consumed = buffer.Length;
                value = len == -1
                    ? new RespValueLite(RedisRespReader.RespKind.NullArray)
                    : new RespValueLite(RedisRespReader.RespKind.Array, default, 0, len);
                return true;
            }
            default:
                throw new InvalidOperationException($"Unsupported RESP prefix: {(char)span[0]}");
        }
    }

    private static bool TryReadLine(ReadOnlySpan<byte> data, out int consumed, out ReadOnlySpan<byte> line)
    {
        var lf = data.IndexOf((byte)'\n');
        if (lf < 0)
        {
            consumed = 0;
            line = default;
            return false;
        }

        var end = lf;
        if (end > 0 && data[end - 1] == (byte)'\r')
            end -= 1;
        line = data.Slice(0, end);
        consumed = lf + 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseInt64(ReadOnlySpan<byte> data)
    {
        long value = 0;
        var negative = false;
        var idx = 0;
        if (idx < data.Length && data[idx] == (byte)'-')
        {
            negative = true;
            idx++;
        }

        for (; idx < data.Length; idx++)
        {
            var c = data[idx];
            if (c is < (byte)'0' or > (byte)'9')
                throw new InvalidOperationException("Invalid integer frame.");
            value = (value * 10) + (c - (byte)'0');
        }

        return negative ? -value : value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseLength(ReadOnlySpan<byte> data, out int value)
    {
        value = 0;
        var negative = false;
        var idx = 0;
        if (idx < data.Length && data[idx] == (byte)'-')
        {
            negative = true;
            idx++;
        }

        for (; idx < data.Length; idx++)
        {
            var c = data[idx];
            if (c is < (byte)'0' or > (byte)'9')
                return false;
            checked
            {
                value = (value * 10) + (c - (byte)'0');
            }
        }

        if (negative)
            value = -value;
        return true;
    }
}
