using System;
using System.Reflection;
using System.Threading.Tasks;
using VapeCache.Abstractions.Connections;
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
    public void TryParseClusterRedirectMessage_ParsesMovedWithExtraWhitespace()
    {
        var method = GetTryParseClusterRedirectMessageMethod();
        var args = new object?[] { "  Redis error:   MOVED   7   cache-node.local:6380   details", null };

        var ok = (bool)method.Invoke(null, args)!;
        Assert.True(ok);
        AssertClusterRedirect(
            args[1],
            isAsk: false,
            slot: 7,
            host: "cache-node.local",
            port: 6380);
    }

    [Fact]
    public void TryParseClusterRedirectMessage_ReturnsFalseForNonRedirect()
    {
        var method = GetTryParseClusterRedirectMessageMethod();
        var args = new object?[] { "ERR unknown command", null };

        var ok = (bool)method.Invoke(null, args)!;

        Assert.False(ok);
    }

    [Fact]
    public void GetServerCertificateValidationCallbackThrowsInProductionWhenAllowInvalidCertEnabled()
    {
        var method = GetServerCertificateValidationCallbackMethod();
        var options = new RedisConnectionOptions
        {
            UseTls = true,
            AllowInvalidCert = true
        };

        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        try
        {
            var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { options }));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNet);
        }
    }

    [Fact]
    public void GetServerCertificateValidationCallbackAllowsDevelopmentWhenAllowInvalidCertEnabled()
    {
        var method = GetServerCertificateValidationCallbackMethod();
        var options = new RedisConnectionOptions
        {
            UseTls = true,
            AllowInvalidCert = true
        };

        var previousDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var previousAspNet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var callback = method.Invoke(null, new object[] { options });
            Assert.NotNull(callback);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnet);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspNet);
        }
    }

    [Fact]
    public void SelectSecondLaneIndex_NeverMatchesPrimaryCandidate()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "SelectSecondLaneIndex",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        for (var laneCount = 2; laneCount <= 16; laneCount++)
        {
            for (var seed = 0; seed < 256; seed++)
            {
                for (var first = 0; first < laneCount; first++)
                {
                    var second = (int)method!.Invoke(null, new object[] { seed, first, laneCount })!;
                    Assert.InRange(second, 0, laneCount - 1);
                    Assert.NotEqual(first, second);
                }
            }
        }
    }

    [Fact]
    public void SelectThirdLaneIndex_AvoidsFirstTwoCandidates_WhenThreeOrMoreLanes()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "SelectThirdLaneIndex",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        for (var laneCount = 3; laneCount <= 16; laneCount++)
        {
            for (var seed = 0; seed < 256; seed++)
            {
                for (var first = 0; first < laneCount; first++)
                {
                    var second = (first + 1) % laneCount;
                    var third = (int)method!.Invoke(null, new object[] { seed, first, second, laneCount })!;
                    Assert.InRange(third, 0, laneCount - 1);
                    Assert.NotEqual(first, third);
                    Assert.NotEqual(second, third);
                }
            }
        }
    }

    [Fact]
    public void ComputeLaneSelectionScore_ProfilesBiasQueueAndInflightDifferently()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "ComputeLaneSelectionScore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var profileType = typeof(RedisCommandExecutor).GetNestedType(
            "LaneSelectionProfile",
            BindingFlags.NonPublic);
        Assert.NotNull(profileType);

        var readProfile = Enum.Parse(profileType!, "Read");
        var writeProfile = Enum.Parse(profileType!, "Write");

        var queueHeavyRead = (int)method!.Invoke(null, new object[] { 6, 4, 128, 0, 0, 0, 0, readProfile })!;
        var queueHeavyWrite = (int)method!.Invoke(null, new object[] { 6, 4, 128, 0, 0, 0, 0, writeProfile })!;
        Assert.True(queueHeavyRead > queueHeavyWrite);

        var inflightHeavyRead = (int)method!.Invoke(null, new object[] { 1, 48, 128, 0, 0, 0, 0, readProfile })!;
        var inflightHeavyWrite = (int)method!.Invoke(null, new object[] { 1, 48, 128, 0, 0, 0, 0, writeProfile })!;
        Assert.True(inflightHeavyWrite > inflightHeavyRead);
    }

    [Fact]
    public void ComputeLaneSelectionScore_PenalizesQueueWaitAndFailureSignals()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "ComputeLaneSelectionScore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var profileType = typeof(RedisCommandExecutor).GetNestedType(
            "LaneSelectionProfile",
            BindingFlags.NonPublic);
        Assert.NotNull(profileType);

        var readProfile = Enum.Parse(profileType!, "Read");
        var baseScore = (int)method!.Invoke(null, new object[] { 1, 4, 128, 100, 0, 0, 0, readProfile })!;
        var withQueueWait = (int)method!.Invoke(null, new object[] { 1, 4, 128, 2_500, 0, 0, 0, readProfile })!;
        var withFailures = (int)method!.Invoke(null, new object[] { 1, 4, 128, 100, 128, 128, 2, readProfile })!;

        Assert.True(withQueueWait > baseScore);
        Assert.True(withFailures > withQueueWait);
    }

    [Fact]
    public void IsLaneWithinBound_RejectsOverloadedLaneForReadProfile()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "IsLaneWithinBound",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var profileType = typeof(RedisCommandExecutor).GetNestedType(
            "LaneSelectionProfile",
            BindingFlags.NonPublic);
        Assert.NotNull(profileType);

        var readProfile = Enum.Parse(profileType!, "Read");
        var healthyBounded = (bool)method!.Invoke(null, new object[] { 1, 8, 128, 0, readProfile })!;
        var overloadedQueue = (bool)method!.Invoke(null, new object[] { 64, 8, 128, 0, readProfile })!;
        var overloadedInflight = (bool)method!.Invoke(null, new object[] { 1, 122, 128, 0, readProfile })!;

        Assert.True(healthyBounded);
        Assert.False(overloadedQueue);
        Assert.False(overloadedInflight);
    }

    [Fact]
    public void ShouldRetryRawPathTransient_ReturnsTrueForLoadingAndEndOfStream()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "ShouldRetryRawPathTransient",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var loading = new InvalidOperationException("LOADING Redis is loading the dataset in memory");
        var endOfStream = new System.IO.EndOfStreamException("Attempted to read past the end of the stream.");

        var loadingResult = (bool)method!.Invoke(null, new object[] { loading, CancellationToken.None })!;
        var eosResult = (bool)method!.Invoke(null, new object[] { endOfStream, CancellationToken.None })!;

        Assert.True(loadingResult);
        Assert.True(eosResult);
    }

    [Fact]
    public void ShouldRetryRawPathTransient_ReturnsFalseWhenCanceled()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "ShouldRetryRawPathTransient",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new TimeoutException("timed out");

        var result = (bool)method!.Invoke(null, new object[] { ex, cts.Token })!;
        Assert.False(result);
    }

    private static ValueTask<byte[]?> InvokeMapGetResponseAsync(RedisRespReader.RespValue resp)
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "MapGetResponseAsync",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(ValueTask<RedisRespReader.RespValue>) },
            modifiers: null);
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

    private static MethodInfo GetServerCertificateValidationCallbackMethod()
    {
        var method = typeof(RedisCommandExecutor).GetMethod(
            "GetServerCertificateValidationCallback",
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
