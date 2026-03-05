using VapeCache.Core.Policies;

namespace VapeCache.Tests.Core;

public sealed class StampedeRuntimePolicyTests
{
    [Fact]
    public void ValidateKey_AllowsAnyValue_WhenSuspiciousKeyChecksDisabled()
    {
        var result = StampedeRuntimePolicy.ValidateKey("bad\u0000key", rejectSuspiciousKeys: false, maxKeyLength: 1);

        Assert.True(result.IsValid);
        Assert.Equal(StampedeKeyRejectionReason.None, result.Reason);
    }

    [Fact]
    public void ValidateKey_RejectsEmptyKey()
    {
        var result = StampedeRuntimePolicy.ValidateKey(string.Empty, rejectSuspiciousKeys: true, maxKeyLength: 512);

        Assert.False(result.IsValid);
        Assert.Equal(StampedeKeyRejectionReason.Empty, result.Reason);
    }

    [Fact]
    public void ValidateKey_RejectsOverMaxLength()
    {
        var result = StampedeRuntimePolicy.ValidateKey("abcd", rejectSuspiciousKeys: true, maxKeyLength: 3);

        Assert.False(result.IsValid);
        Assert.Equal(StampedeKeyRejectionReason.MaxLength, result.Reason);
    }

    [Fact]
    public void ValidateKey_RejectsControlCharacters()
    {
        var result = StampedeRuntimePolicy.ValidateKey("bad\u0000key", rejectSuspiciousKeys: true, maxKeyLength: 512);

        Assert.False(result.IsValid);
        Assert.Equal(StampedeKeyRejectionReason.ControlCharacter, result.Reason);
    }

    [Fact]
    public void ValidateKey_AcceptsValidKey()
    {
        var result = StampedeRuntimePolicy.ValidateKey("valid:key", rejectSuspiciousKeys: true, maxKeyLength: 512);

        Assert.True(result.IsValid);
        Assert.Equal(StampedeKeyRejectionReason.None, result.Reason);
    }

    [Theory]
    [InlineData(false, 500, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 1, true)]
    public void IsFailureBackoffConfigured_UsesExpectedRules(bool enabled, int backoffMs, bool expected)
    {
        var configured = StampedeRuntimePolicy.IsFailureBackoffConfigured(enabled, TimeSpan.FromMilliseconds(backoffMs));

        Assert.Equal(expected, configured);
    }

    [Theory]
    [InlineData(1000, 1000, false)]
    [InlineData(1001, 1000, false)]
    [InlineData(999, 1000, true)]
    public void IsWithinFailureBackoffWindow_UsesUtcTicksComparison(long nowTicks, long retryAfterTicks, bool expected)
    {
        var inWindow = StampedeRuntimePolicy.IsWithinFailureBackoffWindow(nowTicks, retryAfterTicks);

        Assert.Equal(expected, inWindow);
    }

    [Fact]
    public void ComputeRetryAfterUtcTicks_AddsBackoffTicks()
    {
        const long now = 1000;
        var retryAfter = StampedeRuntimePolicy.ComputeRetryAfterUtcTicks(now, TimeSpan.FromTicks(35));

        Assert.Equal(1035, retryAfter);
    }
}
