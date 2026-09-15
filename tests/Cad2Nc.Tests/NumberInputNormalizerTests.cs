using Cad2Nc.App;

namespace Cad2Nc.Tests;

public sealed class NumberInputNormalizerTests
{
    [Theory]
    [InlineData(0.1, 0.1)]
    [InlineData(0.1 + 0.1, 0.2)]
    [InlineData(0.1 + 0.1 + 0.1, 0.3)]
    [InlineData(0.15, 0.2)]
    [InlineData(1.04, 1.0)]
    [InlineData(1.05, 1.1)]
    [InlineData(-0.05, 0.0)]
    public void LeaveSkinUsesOneDecimalAndClampsNegative(double input, double expected)
    {
        Assert.Equal(expected, NumberInputNormalizer.Normalize("LeaveSkin", input), precision: 10);
    }

    [Fact]
    public void InvalidAndOtherFieldsAreNotSilentlyChanged()
    {
        Assert.True(double.IsNaN(NumberInputNormalizer.Normalize("LeaveSkin", double.NaN)));
        Assert.Equal(0.123, NumberInputNormalizer.Normalize("CuttingDepth", 0.123), precision: 10);
    }
}
