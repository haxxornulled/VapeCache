using System;
using System.Reflection;
using System.Threading.Tasks;
using VapeCache.Infrastructure.Connections;
using Xunit;

namespace VapeCache.Tests.Connections;

public sealed class RedisCommandExecutorTests
{
    [Fact]
    public async Task TryGet_MapGetResponse_ThrowsOnError()
    {
        var resp = RedisRespReader.RespValue.Error("ERR noauth");
        var vt = InvokeMapGetResponseAsync(resp);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await vt.ConfigureAwait(false));
    }

    [Fact]
    public async Task TryGet_MapGetResponse_ThrowsOnUnexpectedKind()
    {
        var resp = RedisRespReader.RespValue.Integer(1);
        var vt = InvokeMapGetResponseAsync(resp);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await vt.ConfigureAwait(false));
    }

    private static ValueTask<byte[]?> InvokeMapGetResponseAsync(RedisRespReader.RespValue resp)
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "MapGetResponseAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var valueTask = new ValueTask<RedisRespReader.RespValue>(resp);
        return (ValueTask<byte[]?>)method!.Invoke(null, new object[] { valueTask })!;
    }
}
