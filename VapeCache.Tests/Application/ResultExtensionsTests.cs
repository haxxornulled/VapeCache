using LanguageExt.Common;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Core.Common.Extensions;

namespace VapeCache.Tests.Application;

public sealed class ResultExtensionsTests
{
    [Fact]
    public void HandleError_InvokesHandler_AndReturnsException()
    {
        var called = false;
        var result = new Result<int>(new InvalidOperationException("boom"));

        var ex = result.HandleError(_ => { called = true; });

        Assert.True(called);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void HandleSuccess_InvokesHandler_AndReturnsValue()
    {
        var called = false;
        var result = new Result<int>(5);

        var value = result.HandleSuccess(v => called = v == 5);

        Assert.True(called);
        Assert.Equal(5, value);
    }

    [Fact]
    public void HandleSuccess_Throws_OnFailure()
    {
        var result = new Result<int>(new InvalidOperationException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() => result.HandleSuccess(_ => { }));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("boom", ex.InnerException!.Message);
    }

    [Fact]
    public void HandleError_GenericHandler_ReturnsMappedValue()
    {
        var result = new Result<int>(new InvalidOperationException("boom"));

        var value = result.HandleError<int, string>(error => error.Message);

        Assert.Equal("boom", value);
    }

    [Fact]
    public void HandleError_GenericHandler_ReturnsDefault_OnSuccess()
    {
        var result = new Result<int>(5);

        var value = result.HandleError<int, string>(error => error.Message);

        Assert.Null(value);
    }

    [Fact]
    public void HandleErrorWithLogging_HandlesLogFailures()
    {
        var called = false;
        var result = new Result<int>(new InvalidOperationException("boom"));

        var ex = result.HandleErrorWithLogging(
            _ => { called = true; },
            (_, _) => throw new InvalidOperationException("log failed"),
            NullLogger.Instance);

        Assert.False(called);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void HandleSuccessWithLogging_InvokesHandlers()
    {
        var called = false;
        var result = new Result<string>("ok");

        var value = result.HandleSuccessWithLogging(
            _ => called = true,
            (_, _) => { },
            NullLogger.Instance);

        Assert.True(called);
        Assert.Equal("ok", value);
    }

    [Fact]
    public void HandleError_Throws_OnNullHandler()
    {
        var result = new Result<int>(5);

        Assert.Throws<ArgumentNullException>(() => result.HandleError(null!));
    }
}
