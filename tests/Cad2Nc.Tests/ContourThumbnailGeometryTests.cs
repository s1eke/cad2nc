using Cad2Nc.Application;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class ContourThumbnailGeometryTests
{
    [Fact]
    public void CentersPreservesAspectRatioAndFlipsCadYAxis()
    {
        var points = ContourThumbnailGeometry.Create(Rectangle(0, 0, 20, 10));
        Assert.Equal(new Point2(5, 27.5), points[0]);
        Assert.Equal(new Point2(35, 27.5), points[1]);
        Assert.Equal(new Point2(35, 12.5), points[2]);
        Assert.Equal(points[0], points[^1]);
    }

    [Fact]
    public void LargeCoordinatesHaveTheSameThumbnailAsLocalCoordinates()
    {
        var local = ContourThumbnailGeometry.Create(Rectangle(0, 0, 20, 10));
        var distant = ContourThumbnailGeometry.Create(Rectangle(1e12, -1e12, 20, 10));
        Assert.Equal(local.ToArray(), distant.ToArray());
    }

    [Fact]
    public void VeryThinContourRemainsCenteredAndFinite()
    {
        var points = ContourThumbnailGeometry.Create(Rectangle(0, 0, 0.0001, 100));
        Assert.All(points, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        Assert.Equal(20, (points.Min(point => point.X) + points.Max(point => point.X)) / 2, 10);
        Assert.Equal(5, points.Min(point => point.Y));
        Assert.Equal(35, points.Max(point => point.Y));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(10000)]
    public void DenseContourIsBoundedClosedAndRetainsItsSilhouette(int vertices)
    {
        var ring = Enumerable.Range(0, vertices).Select(index => new Coordinate(
            Math.Cos(index * 2 * Math.PI / vertices), Math.Sin(index * 2 * Math.PI / vertices))).ToList();
        ring.Add(ring[0].Copy());
        var contour = new Contour("C1", "shape", new GeometryFactory().CreatePolygon(ring.ToArray()));
        var points = ContourThumbnailGeometry.Create(contour);
        Assert.InRange(points.Length, 4, ContourThumbnailGeometry.MaximumPointCount);
        Assert.Equal(points[0], points[^1]);
        Assert.InRange(points.Min(point => point.X), 5, 5.02);
        Assert.InRange(points.Max(point => point.X), 34.98, 35);
        Assert.InRange(points.Min(point => point.Y), 5, 5.02);
        Assert.InRange(points.Max(point => point.Y), 34.98, 35);
    }

    [Fact]
    public void UnevenlyTessellatedRectangleRetainsAllFourCorners()
    {
        var ring = Enumerable.Range(0, 10001).Select(index => new Coordinate(index / 100d, 0)).ToList();
        ring.AddRange([new(100, 100), new(0, 100), new(0, 0)]);
        var contour = new Contour("C1", "shape", new GeometryFactory().CreatePolygon(ring.ToArray()));
        var points = ContourThumbnailGeometry.Create(contour);
        Assert.Equal(new Point2[] { new(5, 35), new(35, 35), new(35, 5), new(5, 5), new(5, 35) }, points.ToArray());
    }

    [Fact]
    public void DenseConcaveContourRetainsItsKeyInsetCorners()
    {
        var ring = Enumerable.Range(0, 10001).Select(index => new Coordinate(index / 100d, 0)).ToList();
        ring.AddRange([new(100, 100), new(70, 100), new(70, 30), new(30, 30), new(30, 100), new(0, 100), new(0, 0)]);
        var contour = new Contour("C1", "shape", new GeometryFactory().CreatePolygon(ring.ToArray()));
        var points = ContourThumbnailGeometry.Create(contour);
        Assert.Equal(9, points.Length);
        Assert.Contains(new Point2(26, 26), points);
        Assert.Contains(new Point2(14, 26), points);
        Assert.Equal(points[0], points[^1]);
    }

    [Fact]
    public void PointBudgetStillPreservesBoundingExtremaOfComplexContour()
    {
        const int vertices = 2000;
        var ring = Enumerable.Range(0, vertices).Select(index =>
        {
            var radius = index % 2 == 0 ? 10 : 8;
            return new Coordinate(radius * Math.Cos(index * 2 * Math.PI / vertices), radius * Math.Sin(index * 2 * Math.PI / vertices));
        }).ToList();
        ring.Add(ring[0].Copy());
        var contour = new Contour("C1", "shape", new GeometryFactory().CreatePolygon(ring.ToArray()));
        var points = ContourThumbnailGeometry.Create(contour);
        Assert.Equal(ContourThumbnailGeometry.MaximumPointCount, points.Length);
        Assert.Equal(points[0], points[^1]);
        Assert.Equal(5, points.Min(point => point.X), 10);
        Assert.Equal(35, points.Max(point => point.X), 10);
        Assert.Equal(5, points.Min(point => point.Y), 10);
        Assert.Equal(35, points.Max(point => point.Y), 10);
    }

    private static Contour Rectangle(double x, double y, double width, double height) =>
        new("C1", "shape", new GeometryFactory().CreatePolygon([
            new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height), new(x, y)
        ]));
}
