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

    [Fact]
    public void ParseDouble_AcceptsSimpleString()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "ParseDouble",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var value = RedisRespReader.RespValue.SimpleString("42.125");
        var result = (double)method!.Invoke(null, new object[] { value })!;

        Assert.Equal(42.125, result);
    }

    [Fact]
    public void TryParseClusterRedirectMessage_ParsesMovedTarget()
    {
        var method = GetTryParseClusterRedirectMessageMethod();
        var args = new object?[] { "Redis error: MOVED 1200 10.0.0.5:6381", null };

        var ok = (bool)method.Invoke(null, args)!;
        Assert.True(ok);
        AssertClusterRedirect(
            args[1],
            isAsk: false,
            slot: 1200,
            host: "10.0.0.5",
            port: 6381);
    }

    [Fact]
    public void TryParseClusterRedirectMessage_ParsesAskIPv6Target()
    {
        var method = GetTryParseClusterRedirectMessageMethod();
        var args = new object?[] { "ASK 42 [fe80::1]:6379", null };

        var ok = (bool)method.Invoke(null, args)!;
        Assert.True(ok);
        AssertClusterRedirect(
            args[1],
            isAsk: true,
            slot: 42,
            host: "fe80::1",
            port: 6379);
    }

    [Fact]
    public void TryParseClusterRedirectMessage_ReturnsFalseForNonRedirect()
    {
        var method = GetTryParseClusterRedirectMessageMethod();
        var args = new object?[] { "ERR unknown command", null };

        var ok = (bool)method.Invoke(null, args)!;

        Assert.False(ok);
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

    private static MethodInfo GetTryParseClusterRedirectMessageMethod()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "TryParseClusterRedirectMessage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }

    private static void AssertClusterRedirect(object? redirect, bool isAsk, int slot, string host, int port)
    {
        Assert.NotNull(redirect);
        var type = redirect!.GetType();

        Assert.Equal(isAsk, AssertProperty<bool>(redirect, type, "IsAsk"));
        Assert.Equal(slot, AssertProperty<int>(redirect, type, "Slot"));
        Assert.Equal(host, AssertProperty<string>(redirect, type, "Host"));
        Assert.Equal(port, AssertProperty<int>(redirect, type, "Port"));
    }

    private static T AssertProperty<T>(object instance, Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(instance));
    }
}
