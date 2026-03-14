using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// Default deterministic EF query cache-key builder using SHA-256.
/// </summary>
public sealed class Sha256EfCoreQueryCacheKeyBuilder : IEfCoreQueryCacheKeyBuilder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const string Prefix = "ef:q:v1:";

    /// <inheritdoc />
    public string BuildQueryCacheKey(string providerName, DbCommand command, string? modelIdentity = null)
    {
        ParanoiaThrowGuard.Against.NotNull(command);
        providerName ??= string.Empty;
        modelIdentity ??= string.Empty;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendString(hash, providerName);
        AppendString(hash, modelIdentity);
        AppendInt32(hash, (int)command.CommandType);
        AppendString(hash, command.CommandText ?? string.Empty);

        var parameters = command.Parameters;
        var parameterCount = parameters?.Count ?? 0;
        AppendInt32(hash, parameterCount);
        if (parameterCount == 0 || parameters is null)
        {
            var emptyDigest = hash.GetHashAndReset();
            return string.Concat(Prefix, Convert.ToHexString(emptyDigest));
        }

        for (var i = 0; i < parameterCount; i++)
        {
            if (parameters[i] is not DbParameter parameter)
                continue;

            AppendString(hash, parameter.ParameterName ?? string.Empty);
            AppendInt32(hash, (int)parameter.DbType);
            AppendInt32(hash, (int)parameter.Direction);
            AppendInt32(hash, parameter.Size);
            AppendParameterValue(hash, parameter.Value);
        }

        var digest = hash.GetHashAndReset();
        return string.Concat(Prefix, Convert.ToHexString(digest));
    }

    private static void AppendParameterValue(IncrementalHash hash, object? value)
    {
        if (value is null || value is DBNull)
        {
            AppendByte(hash, 0);
            return;
        }

        switch (value)
        {
            case bool typed:
                AppendByte(hash, 1);
                AppendByte(hash, typed ? (byte)1 : (byte)0);
                return;
            case byte typed:
                AppendByte(hash, 2);
                AppendByte(hash, typed);
                return;
            case sbyte typed:
                AppendByte(hash, 3);
                AppendByte(hash, unchecked((byte)typed));
                return;
            case short typed:
                AppendByte(hash, 4);
                AppendInt16(hash, typed);
                return;
            case ushort typed:
                AppendByte(hash, 5);
                AppendUInt16(hash, typed);
                return;
            case int typed:
                AppendByte(hash, 6);
                AppendInt32(hash, typed);
                return;
            case uint typed:
                AppendByte(hash, 7);
                AppendUInt32(hash, typed);
                return;
            case long typed:
                AppendByte(hash, 8);
                AppendInt64(hash, typed);
                return;
            case ulong typed:
                AppendByte(hash, 9);
                AppendUInt64(hash, typed);
                return;
            case float typed:
                AppendByte(hash, 10);
                AppendInt32(hash, BitConverter.SingleToInt32Bits(typed));
                return;
            case double typed:
                AppendByte(hash, 11);
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(typed));
                return;
            case decimal typed:
                AppendByte(hash, 12);
                var bits = decimal.GetBits(typed);
                for (var i = 0; i < bits.Length; i++)
                    AppendInt32(hash, bits[i]);
                return;
            case Guid typed:
                AppendByte(hash, 13);
                Span<byte> guidBytes = stackalloc byte[16];
                typed.TryWriteBytes(guidBytes);
                hash.AppendData(guidBytes);
                return;
            case DateTime typed:
                AppendByte(hash, 14);
                AppendInt64(hash, typed.Ticks);
                AppendInt32(hash, (int)typed.Kind);
                return;
            case DateTimeOffset typed:
                AppendByte(hash, 15);
                AppendInt64(hash, typed.Ticks);
                AppendInt32(hash, (int)typed.Offset.Ticks);
                return;
            case TimeSpan typed:
                AppendByte(hash, 16);
                AppendInt64(hash, typed.Ticks);
                return;
            case string typed:
                AppendByte(hash, 17);
                AppendString(hash, typed);
                return;
            case byte[] typed:
                AppendByte(hash, 18);
                AppendInt32(hash, typed.Length);
                hash.AppendData(typed);
                return;
            case char typed:
                AppendByte(hash, 19);
                AppendUInt16(hash, typed);
                return;
            case Enum typed:
                AppendByte(hash, 20);
                AppendString(hash, typed.GetType().FullName ?? string.Empty);
                AppendInt64(hash, Convert.ToInt64(typed, CultureInfo.InvariantCulture));
                return;
            default:
                AppendByte(hash, 127);
                AppendString(hash, value.GetType().FullName ?? string.Empty);
                if (value is IFormattable formattable)
                    AppendString(hash, formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty);
                else
                    AppendString(hash, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        if (value.Length == 0)
        {
            AppendInt32(hash, 0);
            return;
        }

        var maxByteCount = Utf8.GetMaxByteCount(value.Length);
        byte[]? rented = null;
        Span<byte> destination = maxByteCount <= 512
            ? stackalloc byte[maxByteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(maxByteCount));

        try
        {
            var bytesWritten = Utf8.GetBytes(value.AsSpan(), destination);
            AppendInt32(hash, bytesWritten);
            hash.AppendData(destination[..bytesWritten]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }

    private static void AppendInt16(IncrementalHash hash, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt16(IncrementalHash hash, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
