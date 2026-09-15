using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class ContourDeduplicationTests
{
    private static Polygon Shape(double x, double y, bool extraVertex = false, double noise = 0)
    {
        var points = new List<Coordinate> { new(x, y), new(x + 100, y), new(x + 100, y + 100) };
        if (extraVertex) points.Add(new(x + 50, y + 100 + noise));
        points.Add(new(x, y + 100));
        points.Add(new(x, y));
        return new GeometryFactory().CreatePolygon(points.ToArray());
    }

    private static CadGeometryExtractor.RingRecord Ring(Polygon polygon, string layer = "柜体") => new(layer, polygon, ["LWPOLYLINE"]);

    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(0, 0, true, 0)]
    [InlineData(0, 0, true, 1e-10)]
    [InlineData(597029.109, 62415.298, false, 1e-10)]
    [InlineData(1e9, 1e9, true, 0)]
    public void EquivalentExtractedRingsMergeWithoutChangingMachiningGeometry(double x, double y, bool reverse, double noise)
    {
        var original = Shape(x, y);
        var binary = original.AsBinary();
        var duplicate = Shape(x, y, true, noise);
        if (reverse) duplicate = (Polygon)duplicate.Reverse();
        var result = new CadGeometryExtractor().MergeDuplicateRings([Ring(original), Ring(duplicate), Ring(duplicate)]);
        var retained = Assert.Single(result);
        Assert.Equal(2, retained.MergedCount);
        Assert.Same(original, retained.Polygon);
        Assert.Equal(binary, retained.Polygon.AsBinary());
    }

    [Theory]
    [InlineData(0.00001)]
    [InlineData(0.001)]
    public void ActualSmallDifferencesArePreserved(double difference)
    {
        var result = new CadGeometryExtractor().MergeDuplicateRings([
            Ring(Shape(0, 0)), Ring(Shape(0, 0, true, difference)), Ring(Shape(difference, 0))]);
        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal(0, r.MergedCount));
    }

    [Fact]
    public void HoleAndDifferentShapeWithSameEnvelopeArePreserved()
    {
        var factory = new GeometryFactory();
        var inner = factory.CreatePolygon([new(10, 10), new(90, 10), new(90, 90), new(10, 90), new(10, 10)]);
        var triangle = factory.CreatePolygon([new(0, 0), new(100, 0), new(100, 100), new(0, 0)]);
        var result = new CadGeometryExtractor().MergeDuplicateRings([Ring(Shape(0, 0)), Ring(inner), Ring(triangle)]);
        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal(0, r.MergedCount));
    }

    [Fact]
    public void EquivalentRingsAcrossLayersStillRejectImport()
    {
        Assert.Contains("Identical", Assert.Throws<CadImportException>(() => new CadGeometryExtractor()
            .MergeDuplicateRings([Ring(Shape(0, 0), "A"), Ring(Shape(0, 0, true), "B")])).Message);
    }

    [Fact]
    public void DuplicateComparisonHonorsCancellationAndResourceBudget()
    {
        var extractor = new CadGeometryExtractor(new() { Limits = new() { MaxTopologyCandidates = 1 } });
        var rings = new List<CadGeometryExtractor.RingRecord> { Ring(Shape(0, 0)), Ring(Shape(0, 0, true)), Ring(Shape(0, 0, true)) };
        Assert.Throws<OperationCanceledException>(() => extractor.MergeDuplicateRings(rings, new CancellationToken(true)));
        Assert.Contains("budget", Assert.Throws<CadImportException>(() => extractor.MergeDuplicateRings(rings)).Message);
    }
}
