using System.Buffers.Binary;
using System.Text;

namespace VapeCache.Console.GroceryStore;

internal static class SessionBinaryCodec
{
    private const uint Magic = 0x56435331; // "VCS1"
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static byte[] Serialize(in UserSession session)
    {
        var recentlyViewed = session.RecentlyViewedProductIds ?? Array.Empty<string>();
        var size = sizeof(uint) + sizeof(long) + sizeof(long) + sizeof(int) + sizeof(byte);
        size += GetEncodedStringSize(session.UserId);
        size += GetEncodedStringSize(session.SessionId);

        for (var i = 0; i < recentlyViewed.Length; i++)
            size += GetEncodedStringSize(recentlyViewed[i]);

        if (session.ActiveCartId is not null)
            size += GetEncodedStringSize(session.ActiveCartId);

        var buffer = GC.AllocateUninitializedArray<byte>(size);
        var span = buffer.AsSpan();
        var offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), Magic);
        offset += sizeof(uint);
        offset += WriteEncodedString(span.Slice(offset), session.UserId);
        offset += WriteEncodedString(span.Slice(offset), session.SessionId);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), session.CreatedAt.Ticks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), session.LastActivityAt.Ticks);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), recentlyViewed.Length);
        offset += sizeof(int);

        for (var i = 0; i < recentlyViewed.Length; i++)
            offset += WriteEncodedString(span.Slice(offset), recentlyViewed[i]);

        if (session.ActiveCartId is null)
        {
            span[offset] = 0;
            return buffer;
        }

        span[offset++] = 1;
        _ = WriteEncodedString(span.Slice(offset), session.ActiveCartId);
        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out UserSession session)
    {
        session = default!;
        if (payload.Length < sizeof(uint))
            return false;

        var offset = 0;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, sizeof(uint)));
        if (magic != Magic)
            return false;
        offset += sizeof(uint);

        if (!TryReadEncodedString(payload, ref offset, out var userId))
            return false;
        if (!TryReadEncodedString(payload, ref offset, out var sessionId))
            return false;
        if (!TryReadInt64(payload, ref offset, out var createdTicks))
            return false;
        if (!TryReadInt64(payload, ref offset, out var lastActivityTicks))
            return false;

        if (!TryReadInt32(payload, ref offset, out var viewedCount) || viewedCount < 0)
            return false;

        var recentlyViewed = viewedCount == 0 ? Array.Empty<string>() : new string[viewedCount];
        for (var i = 0; i < viewedCount; i++)
        {
            if (!TryReadEncodedString(payload, ref offset, out var productId))
                return false;
            recentlyViewed[i] = productId;
        }

        if (offset >= payload.Length)
            return false;

        string? activeCartId = null;
        var hasActiveCart = payload[offset++] != 0;
        if (hasActiveCart)
        {
            if (!TryReadEncodedString(payload, ref offset, out var cartId))
                return false;
            activeCartId = cartId;
        }

        if (!IsValidTicks(createdTicks) || !IsValidTicks(lastActivityTicks))
            return false;

        session = new UserSession(
            userId,
            sessionId,
            new DateTime(createdTicks, DateTimeKind.Utc),
            new DateTime(lastActivityTicks, DateTimeKind.Utc),
            recentlyViewed,
            activeCartId);
        return true;
    }

    private static bool IsValidTicks(long ticks)
        => ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks;

    private static int GetEncodedStringSize(string value)
        => sizeof(int) + Utf8.GetByteCount(value);

    private static int WriteEncodedString(Span<byte> destination, string value)
    {
        var byteCount = Utf8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(0, sizeof(int)), byteCount);
        if (byteCount == 0)
            return sizeof(int);

        _ = Utf8.GetBytes(value, destination.Slice(sizeof(int), byteCount));
        return sizeof(int) + byteCount;
    }

    private static bool TryReadEncodedString(ReadOnlySpan<byte> payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (!TryReadInt32(payload, ref offset, out var byteCount) || byteCount < 0)
            return false;

        if (payload.Length - offset < byteCount)
            return false;

        value = byteCount == 0
            ? string.Empty
            : Utf8.GetString(payload.Slice(offset, byteCount));
        offset += byteCount;
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> payload, ref int offset, out int value)
    {
        value = 0;
        if (payload.Length - offset < sizeof(int))
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return true;
    }

    private static bool TryReadInt64(ReadOnlySpan<byte> payload, ref int offset, out long value)
    {
        value = 0;
        if (payload.Length - offset < sizeof(long))
            return false;

        value = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        return true;
    }
}
