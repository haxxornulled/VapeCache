using VapeCache.Infrastructure.Connections;

namespace VapeCache.Tests.Connections;

public sealed class RespParserLiteTests
{
    [Fact]
    public void TryParse_simple_string()
    {
        var frame = "+OK\r\n"u8.ToArray();

        var ok = RespParserLite.TryParse(frame, out var consumed, out var value);

        Assert.True(ok);
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(RedisRespReader.RespKind.SimpleString, value.Kind);
        Assert.Equal("OK", System.Text.Encoding.UTF8.GetString(value.Data.Span));
    }

    [Fact]
    public void TryParse_integer()
    {
        var frame = ":-42\r\n"u8.ToArray();

        var ok = RespParserLite.TryParse(frame, out var consumed, out var value);

        Assert.True(ok);
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(RedisRespReader.RespKind.Integer, value.Kind);
        Assert.Equal(-42, value.Integer);
    }

    [Fact]
    public void TryParse_bulk_string_and_null()
    {
        var bulk = "$3\r\nfoo\r\n"u8.ToArray();
        var nullBulk = "$-1\r\n"u8.ToArray();

        Assert.True(RespParserLite.TryParse(bulk, out var consumedBulk, out var bulkValue));
        Assert.Equal(bulk.Length, consumedBulk);
        Assert.Equal(RedisRespReader.RespKind.BulkString, bulkValue.Kind);
        Assert.Equal("foo", System.Text.Encoding.UTF8.GetString(bulkValue.Data.Span));

        Assert.True(RespParserLite.TryParse(nullBulk, out var consumedNull, out var nullValue));
        Assert.Equal(nullBulk.Length, consumedNull);
        Assert.Equal(RedisRespReader.RespKind.NullBulkString, nullValue.Kind);
    }

    [Fact]
    public void TryParse_array_and_null_array()
    {
        var array = "*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8.ToArray();
        var nullArray = "*-1\r\n"u8.ToArray();

        Assert.True(RespParserLite.TryParse(array, out var consumedArray, out var arrayValue));
        Assert.Equal(array.Length, consumedArray);
        Assert.Equal(RedisRespReader.RespKind.Array, arrayValue.Kind);
        Assert.Equal(2, arrayValue.ArrayLength);

        Assert.True(RespParserLite.TryParse(nullArray, out var consumedNull, out var nullValue));
        Assert.Equal(nullArray.Length, consumedNull);
        Assert.Equal(RedisRespReader.RespKind.NullArray, nullValue.Kind);
    }

    [Fact]
    public void TryParse_returns_false_for_incomplete_frame()
    {
        var incomplete = "$5\r\nab"u8.ToArray();

        var ok = RespParserLite.TryParse(incomplete, out var consumed, out _);

        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryParse_throws_for_unknown_prefix()
    {
        var frame = "!oops\r\n"u8.ToArray();

        Assert.Throws<InvalidOperationException>(() => RespParserLite.TryParse(frame, out _, out _));
    }
}
