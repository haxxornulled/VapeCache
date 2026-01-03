using VapeCache.Application.Guards;

namespace VapeCache.Tests.Application;

public sealed class GuardTests
{
    private enum SampleEnum
    {
        A = 1,
        B = 2
    }

    [Fact]
    public void NotNullOrEmpty_Throws_OnNullOrEmpty()
    {
        Assert.Throws<ArgumentException>(() => Guard.Against.NotNullOrEmpty(null, "value"));
        Assert.Throws<ArgumentException>(() => Guard.Against.NotNullOrEmpty(string.Empty, "value"));
    }

    [Fact]
    public void NotNullOrEmpty_Returns_Value()
    {
        var value = Guard.Against.NotNullOrEmpty("ok", "value");
        Assert.Equal("ok", value);
    }

    [Fact]
    public void NotNullOrWhiteSpace_Throws_OnInvalid()
    {
        Assert.Throws<ArgumentException>(() => Guard.Against.NotNullOrWhiteSpace(null, "value"));
        Assert.Throws<ArgumentException>(() => Guard.Against.NotNullOrWhiteSpace("   ", "value"));
    }

    [Fact]
    public void NotNull_Returns_Value()
    {
        int? value = 7;
        var result = Guard.Against.NotNull(value, "value");
        Assert.Equal(7, result);
    }

    [Fact]
    public void NotNull_Throws_OnNull()
    {
        int? value = null;
        Assert.Throws<ArgumentNullException>(() => Guard.Against.NotNull(value, "value"));
    }

    [Fact]
    public void NotOutOfRange_Throws_OnInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Against.NotOutOfRange(0, 1, 3, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Against.NotOutOfRange(4, 1, 3, "value"));
    }

    [Fact]
    public void ValidEnumValue_Throws_OnInvalid()
    {
        Assert.Throws<ArgumentException>(() => Guard.Against.ValidEnumValue((SampleEnum)42, "value"));
    }

    [Fact]
    public void ValidEnumValue_Returns_Value()
    {
        var value = Guard.Against.ValidEnumValue(SampleEnum.B, "value");
        Assert.Equal(SampleEnum.B, value);
    }
}
