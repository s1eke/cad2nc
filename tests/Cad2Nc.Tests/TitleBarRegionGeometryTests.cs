using Cad2Nc.App;

namespace Cad2Nc.Tests;

public sealed class TitleBarRegionGeometryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ButtonsRemainInteractiveAndSurroundingSpaceRemainsDraggableAtEveryDpi(double scale)
    {
        var regions = TitleBarRegionGeometry.Create(240, 48, scale, 0, 0,
            [new(40, 8, 60, 32), new(120, 8, 80, 32)]);

        Assert.Equal(2, regions.Passthrough.Length);
        Assert.Contains(new TitleBarPixelRect((int)(40 * scale), (int)(8 * scale), (int)(60 * scale), (int)(32 * scale)), regions.Passthrough);
        Assert.Contains(new TitleBarPixelRect((int)(120 * scale), (int)(8 * scale), (int)(80 * scale), (int)(32 * scale)), regions.Passthrough);
        AssertCaptionAt(regions, 60 * scale, 4 * scale);
        AssertCaptionAt(regions, 60 * scale, 44 * scale);
        AssertCaptionAt(regions, 110 * scale, 24 * scale);
        AssertCaptionAt(regions, 20 * scale, 24 * scale);
        AssertCaptionAt(regions, 220 * scale, 24 * scale);
        AssertPartition(regions, 0, 0, (int)(240 * scale), (int)(48 * scale));
    }

    [Fact]
    public void SystemInsetsArePhysicalPixelsAndProtectBothSystemRegions()
    {
        var regions = TitleBarRegionGeometry.Create(100, 40, 1.5, 13, 29,
            [new(-10, 8, 30, 20), new(70, 8, 50, 20)]);

        Assert.Equal(new TitleBarPixelRect[] { new(13, 12, 17, 30), new(105, 12, 16, 30) }, regions.Passthrough);
        AssertPartition(regions, 13, 0, 108, 60);
    }

    [Fact]
    public void FractionalButtonEdgesRoundOutwardsAndHeaderEdgesStayInside()
    {
        var regions = TitleBarRegionGeometry.Create(100.7, 30.9, 1.25, 0, 0,
            [new(10.1, 5.1, 20.2, 10.2)]);

        Assert.Equal(new TitleBarPixelRect(12, 6, 26, 14), Assert.Single(regions.Passthrough));
        AssertCaptionAt(regions, 11, 6);
        AssertCaptionAt(regions, 38, 6);
        AssertCaptionAt(regions, 12, 5);
        AssertCaptionAt(regions, 12, 20);
        AssertPartition(regions, 0, 0, 125, 38);
    }

    [Fact]
    public void OverlappingAndOutOfBoundsButtonsAreClippedWithoutLosingOtherCaptionPixels()
    {
        var regions = TitleBarRegionGeometry.Create(100, 40, 1, 0, 0,
            [new(-10, -10, 40, 30), new(20, 10, 40, 20), new(90, 30, 40, 20), new(110, 10, 10, 10)]);

        Assert.Equal(new TitleBarPixelRect[] { new(0, 0, 30, 20), new(20, 10, 40, 20), new(90, 30, 10, 10) }, regions.Passthrough);
        AssertCaptionAt(regions, 40, 5);
        AssertCaptionAt(regions, 70, 20);
        AssertPartition(regions, 0, 0, 100, 40);
    }

    [Fact]
    public void NoButtonsLeavesTheWholeAvailableHeaderDraggable()
    {
        var regions = TitleBarRegionGeometry.Create(100, 40, 1.25, 5, 20, []);

        Assert.Equal(new TitleBarPixelRect(5, 0, 100, 50), Assert.Single(regions.Caption));
        Assert.Empty(regions.Passthrough);
    }

    [Fact]
    public void ButtonCoveringWholeHeaderLeavesNoCaption()
    {
        var regions = TitleBarRegionGeometry.Create(100, 40, 1, 5, 20, [new(-10, -10, 120, 60)]);

        Assert.Empty(regions.Caption);
        Assert.Equal(new TitleBarPixelRect(5, 0, 75, 40), Assert.Single(regions.Passthrough));
    }

    [Theory]
    [InlineData(0, 40, 1)]
    [InlineData(100, 0, 1)]
    [InlineData(-1, 40, 1)]
    [InlineData(100, 40, 0)]
    [InlineData(100, 40, -1)]
    [InlineData(double.NaN, 40, 1)]
    [InlineData(100, double.PositiveInfinity, 1)]
    [InlineData(100, 40, double.NaN)]
    [InlineData(100, 40, double.PositiveInfinity)]
    [InlineData(0.1, 0.1, 1)]
    [InlineData(double.MaxValue, 40, 2)]
    public void UnavailableOrInvalidLayoutProducesEmptyRegions(double width, double height, double scale)
    {
        var regions = TitleBarRegionGeometry.Create(width, height, scale, 0, 0, [new(1, 1, 20, 20)]);

        Assert.Empty(regions.Caption);
        Assert.Empty(regions.Passthrough);
    }

    [Theory]
    [InlineData(20, 10, 10)]
    [InlineData(20, 15, 15)]
    [InlineData(20, int.MaxValue, 0)]
    [InlineData(20, 0, int.MaxValue)]
    public void VeryNarrowHeaderNeverOverwritesSystemInsets(double width, int leftInset, int rightInset)
    {
        var regions = TitleBarRegionGeometry.Create(width, 40, 1, leftInset, rightInset, [new(0, 0, 100, 40)]);

        Assert.Empty(regions.Caption);
        Assert.Empty(regions.Passthrough);
    }

    [Fact]
    public void InvalidButtonBoundsAreIgnoredAndNegativeInsetsAreTreatedAsZero()
    {
        var regions = TitleBarRegionGeometry.Create(100, 40, 1, -10, -20,
            [new(double.NaN, 0, 20, 20), new(0, double.PositiveInfinity, 20, 20),
                new(0, 0, double.PositiveInfinity, 20), new(0, 0, 20, double.NaN),
                new(0, 0, -20, 20), new(0, 0, 20, 0)]);

        Assert.Equal(new TitleBarPixelRect(0, 0, 100, 40), Assert.Single(regions.Caption));
        Assert.Empty(regions.Passthrough);
    }

    private static void AssertCaptionAt(TitleBarRegions regions, double x, double y)
    {
        Assert.Single(regions.Caption, rect => Contains(rect, (int)x, (int)y));
        Assert.DoesNotContain(regions.Passthrough, rect => Contains(rect, (int)x, (int)y));
    }

    private static void AssertPartition(TitleBarRegions regions, int x, int y, int width, int height)
    {
        Assert.All(regions.Caption.Concat(regions.Passthrough), rect =>
        {
            Assert.True(rect.Width > 0 && rect.Height > 0);
            Assert.InRange(rect.X, x, x + width - 1);
            Assert.InRange(rect.Y, y, y + height - 1);
            Assert.InRange(rect.X + rect.Width, x + 1, x + width);
            Assert.InRange(rect.Y + rect.Height, y + 1, y + height);
        });

        for (var pixelY = y; pixelY < y + height; pixelY++)
        for (var pixelX = x; pixelX < x + width; pixelX++)
        {
            var interactive = regions.Passthrough.Any(rect => Contains(rect, pixelX, pixelY));
            var captionCount = regions.Caption.Count(rect => Contains(rect, pixelX, pixelY));
            Assert.Equal(interactive ? 0 : 1, captionCount);
        }
    }

    private static bool Contains(TitleBarPixelRect rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
}
