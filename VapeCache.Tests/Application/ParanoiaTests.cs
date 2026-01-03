using VapeCache.Application.Guards;

namespace VapeCache.Tests.Application;

public sealed class ParanoiaTests
{
    [Fact]
    public void Validate_String_Fails_OnNullOrEmpty()
    {
        var nullResult = Paranoia.Validate((string?)null, "name");
        var emptyResult = Paranoia.Validate(string.Empty, "name");

        Assert.True(nullResult.IsFail);
        Assert.True(emptyResult.IsFail);
    }

    [Fact]
    public void Validate_StringArray_Fails_WhenOnlyWhitespace()
    {
        var result = Paranoia.Validate(new[] { " ", "", "  " }, "items");
        Assert.True(result.IsFail);
    }

    [Fact]
    public void Validate_StringArray_Succeeds_WithNonEmptyEntry()
    {
        var result = Paranoia.Validate(new[] { " ", "alpha", "alpha" }, "items");
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_Dictionary_Fails_OnEmptyKey()
    {
        var result = Paranoia.Validate(new Dictionary<string, int> { [""] = 1 }, "map");
        Assert.True(result.IsFail);
    }

    [Fact]
    public void Validate_NonNegativeTimeSpan_Fails_OnNegative()
    {
        var result = Paranoia.ValidateNonNegativeTimeSpan(TimeSpan.FromSeconds(-1), "duration");
        Assert.True(result.IsFail);
    }

    [Fact]
    public void Validate_ReadOnlyMemory_Fails_OnEmpty()
    {
        var result = Paranoia.Validate(ReadOnlyMemory<byte>.Empty, "payload");
        Assert.True(result.IsFail);
    }
}
