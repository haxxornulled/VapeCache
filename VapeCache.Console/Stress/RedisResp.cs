using System.Buffers;
using System.Text;
using LanguageExt.Common;
using VapeCache.Application.Connections;

namespace VapeCache.Console.Stress;

internal static class RedisResp
{
    public static byte[] BuildCommand(params string[] parts)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(parts.Length).Append("\r\n");
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            sb.Append('$').Append(bytes.Length).Append("\r\n");
            sb.Append(part).Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static async Task<string> ReadLineAsync(IRedisConnection conn, CancellationToken ct)
    {
        var one = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            var bytes = new List<byte>(64);
            while (true)
            {
                var read = await conn.ReceiveAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
                if (!read.IsSuccess)
                    throw read.Match(_ => new Exception("Receive failed."), ex => ex);

                var n = read.Match(v => v, _ => 0);
                if (n == 0) throw new EndOfStreamException();

                bytes.Add(one[0]);
                var count = bytes.Count;
                if (count >= 2 && bytes[count - 2] == (byte)'\r' && bytes[count - 1] == (byte)'\n')
                    return Encoding.UTF8.GetString(bytes.ToArray(), 0, count - 2);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(one);
        }
    }
}

