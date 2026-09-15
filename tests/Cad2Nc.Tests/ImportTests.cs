using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using Cad2Nc.Core;
using CSMath;
using DwgDocument = ACadSharp.CadDocument;

namespace Cad2Nc.Tests;

public sealed class ImportTests
{
    private static DwgDocument Drawing()
    {
        var drawing = new DwgDocument(); drawing.Header.InsUnits = UnitsType.Millimeters; return drawing;
    }

    private static LwPolyline Rectangle(double x = 0, double y = 0, double width = 10, double height = 5, Layer? layer = null)
    {
        var line = new LwPolyline { IsClosed = true };
        if (layer is not null) line.Layer = layer;
        line.Vertices.AddRange([new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)]);
        return line;
    }

    private static CadImportResult Extract(DwgDocument drawing, ImportOptions? options = null) => new CadGeometryExtractor(options).Extract(drawing);

    [Fact]
    public void SheetFrameIsOptInButInnerPartsAreSelected()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle(width: 20, height: 10));
        drawing.Entities.Add(new Circle { Center = new(5, 5, 0), Radius = 2 });
        drawing.Entities.Add(new Circle { Center = new(15, 5, 0), Radius = 2 });
        var document = Extract(drawing).Document;
        Assert.Equal(3, document.Contours.Length);
        Assert.True(document.Contours[0].IsOuterCandidate);
        Assert.False(document.DefaultSelection[document.Contours[0].Id].Included);
        Assert.All(document.Contours.Skip(1), c => Assert.True(document.DefaultSelection[c.Id].Included));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "geometry.duplicate_contour");
    }

    [Fact]
    public void OpenConstructionLayerDoesNotSplitClosedCutLayer()
    {
        var drawing = Drawing(); var cut = new Layer("CUT"); var center = new Layer("CENTER");
        drawing.Layers.Add(cut); drawing.Layers.Add(center);
        drawing.Entities.Add(Rectangle(layer: cut));
        drawing.Entities.Add(new Line { StartPoint = new(-5, 2, 0), EndPoint = new(15, 2, 0), Layer = center });
        var result = Extract(drawing);
        Assert.Single(result.Document.Contours);
        Assert.Equal("CUT", result.Document.Contours[0].Layer);
        Assert.Contains(result.Diagnostics, d => d.Code == "geometry.open_linework" && d.RequiresConfirmation && d.Message.Contains("CENTER"));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 15)]
    public void SameLayerChordCannotInventBoundaries(double start, double end)
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle(height: 10));
        drawing.Entities.Add(new Line { StartPoint = new(start, 5, 0), EndPoint = new(end, 5, 0) });
        Assert.Throws<CadImportException>(() => Extract(drawing));
    }

    [Fact]
    public void NearSharedEndpointWithinSnapToleranceDoesNotCountAsCrossing()
    {
        var drawing = Drawing();
        const double endpointNoise = 1e-8;
        drawing.Entities.Add(new Line { StartPoint = new(0, 0, 0), EndPoint = new(10 + endpointNoise, 0, 0) });
        drawing.Entities.Add(new Line { StartPoint = new(10, 0, 0), EndPoint = new(10, 10, 0) });
        drawing.Entities.Add(new Line { StartPoint = new(10, 10, 0), EndPoint = new(0, 10, 0) });
        drawing.Entities.Add(new Line { StartPoint = new(0, 10, 0), EndPoint = new(0, 0, 0) });

        var contour = Assert.Single(Extract(drawing).Document.Contours);
        Assert.Equal(100, contour.Area, 6);
    }

    [Fact]
    public void NearIntersectionAtOnlyOneLineEndpointStillRejectsCrossing()
    {
        var drawing = Drawing();
        drawing.Entities.Add(new Line { StartPoint = new(0, 0, 0), EndPoint = new(10, 0, 0) });
        drawing.Entities.Add(new Line { StartPoint = new(5, 1e-8, 0), EndPoint = new(5, 10, 0) });

        Assert.Contains("branching", Assert.Throws<CadImportException>(() => Extract(drawing)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TinyChordEndSegmentsDoNotHideBranchNodes()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle(height: 10));
        var chord = new LwPolyline(); chord.Vertices.AddRange([new(0, 5), new(.0005, 5), new(9.9995, 5), new(10, 5)]); drawing.Entities.Add(chord);
        Assert.Contains("branching", Assert.Throws<CadImportException>(() => Extract(drawing)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelfIntersectionIsRejectedBeforePolygonizing()
    {
        var drawing = Drawing(); var bowTie = new LwPolyline { IsClosed = true };
        bowTie.Vertices.AddRange([new(0, 0), new(10, 10), new(0, 10), new(10, 0)]); drawing.Entities.Add(bowTie);
        Assert.Contains("Self-intersecting", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void OverlappingClosedContoursAreRejected()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle()); drawing.Entities.Add(Rectangle(x: 5));
        Assert.Contains("overlapping", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void IdenticalContoursAcrossLayersAreRejected()
    {
        var drawing = Drawing(); var a = new Layer("A"); var b = new Layer("B"); drawing.Layers.Add(a); drawing.Layers.Add(b);
        drawing.Entities.Add(Rectangle(layer: a)); drawing.Entities.Add(Rectangle(layer: b));
        Assert.Contains("Identical", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void InchCoordinatesAreConvertedAndUnknownUnitsNeedConfirmation()
    {
        var drawing = Drawing(); drawing.Header.InsUnits = UnitsType.Inches; drawing.Entities.Add(Rectangle(width: 1, height: 1));
        var inch = Extract(drawing); Assert.Equal(25.4, inch.Document.Contours[0].Bounds.Width, 8);
        Assert.Contains(inch.Diagnostics, d => d.Code == "units.converted");
        drawing.Header.InsUnits = UnitsType.Unitless;
        var unknown = Extract(drawing); Assert.True(unknown.RequiresUnitsConfirmation); Assert.Equal(1, unknown.Document.Contours[0].Bounds.Width, 8);
    }

    [Fact]
    public void LargeOriginKeepsSubmillimetreEdges()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle(1e9, 1e9, .5, 1));
        var contour = Assert.Single(Extract(drawing).Document.Contours); Assert.Equal(.5, contour.Area, 8); Assert.Equal(.5, contour.Bounds.Width, 8);
    }

    [Fact]
    public void OversizedCurveIsRejectedBeforeTessellation()
    {
        var drawing = Drawing(); drawing.Entities.Add(new Circle { Radius = 1e12 });
        Assert.Contains("physical bounds", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void NonPlanarPolylineAndMultiplePlanesAreRejected()
    {
        var drawing = Drawing(); drawing.Entities.Add(new Polyline3D(new XYZ[] { new(0, 0, 0), new(10, 0, 5), new(10, 10, 0), new(0, 10, -5) }, true));
        Assert.Contains("Non-planar", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
        drawing = Drawing(); drawing.Entities.Add(Rectangle()); var other = Rectangle(20); other.Elevation = 1; drawing.Entities.Add(other);
        Assert.Contains("multiple Z", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void NonzeroPlanarElevationRequiresReview()
    {
        var drawing = Drawing(); var rectangle = Rectangle(); rectangle.Elevation = 5; drawing.Entities.Add(rectangle);
        Assert.Contains(Extract(drawing).Diagnostics, d => d.Code == "geometry.elevation" && d.RequiresConfirmation);
    }

    [Fact]
    public void NegativeNormalUsesOcsAndBulgeCreatesSemicircle()
    {
        var drawing = Drawing(); var line = new LwPolyline { IsClosed = true, Normal = new(0, 0, -1) };
        line.Vertices.AddRange([new(0, 0) { Bulge = 1 }, new(10, 0)]); drawing.Entities.Add(line);
        var contour = Assert.Single(Extract(drawing).Document.Contours);
        Assert.Equal(-10, contour.Bounds.MinX, 7); Assert.Equal(0, contour.Bounds.MaxX, 7);
        Assert.InRange(contour.Area, 38.8, Math.PI * 25 / 2);
    }

    [Fact]
    public void InsertAndCircleOcsApplyToTheirNonzeroOrigins()
    {
        var drawing = Drawing(); var block = new BlockRecord("OCS"); block.Entities.Add(Rectangle(width: 1, height: 1));
        var insert = new Insert(block) { InsertPoint = new(10, 20, 0), Normal = new(0, 0, -1) };
        drawing.BlockRecords.Add(block); drawing.Entities.Add(insert);
        drawing.Entities.Add(new Circle { Center = new(30, 20, 0), Radius = 2, Normal = new(0, 0, -1) });
        var contours = Extract(drawing).Document.Contours;
        var rectangle = Assert.Single(contours.Where(c => Math.Abs(c.Area - 1) < 1e-8));
        Assert.Equal(-11, rectangle.Bounds.MinX, 8); Assert.Equal(-10, rectangle.Bounds.MaxX, 8); Assert.Equal(20, rectangle.Bounds.MinY, 8);
        var circle = Assert.Single(contours.Where(c => c.Area > 1));
        Assert.Equal(-32, circle.Bounds.MinX, 8); Assert.Equal(-28, circle.Bounds.MaxX, 8); Assert.Equal(18, circle.Bounds.MinY, 8);
    }

    [Fact]
    public void NestedNonuniformScaledRotatedBlockArraysRespectBasePointAndLayerInheritance()
    {
        var drawing = Drawing(); var cut = new Layer("CUT"); drawing.Layers.Add(cut);
        var inner = new BlockRecord("INNER"); inner.BlockEntity.BasePoint = new(1, 1, 0); inner.Entities.Add(Rectangle(1, 1, 2, 1));
        var wrapper = new BlockRecord("WRAPPER"); wrapper.Entities.Add(new Insert(inner) { InsertPoint = new(2, 3, 0), XScale = 2, YScale = 3, Rotation = Math.PI / 2 });
        drawing.BlockRecords.Add(inner); drawing.BlockRecords.Add(wrapper);
        drawing.Entities.Add(new Insert(wrapper) { InsertPoint = new(100, 200, 0), XScale = 3, YScale = 2, Rotation = Math.PI / 2,
            ColumnCount = 2, ColumnSpacing = 10, RowCount = 2, RowSpacing = 20, Layer = cut });
        var contours = Extract(drawing).Document.Contours;
        Assert.Equal(4, contours.Length); Assert.All(contours, c => { Assert.Equal("CUT", c.Layer); Assert.Equal(72, c.Area, 6); Assert.Equal(8, c.Bounds.Width, 6); Assert.Equal(9, c.Bounds.Height, 6); });
        Assert.Equal(66, contours.Min(c => c.Bounds.MinX), 6); Assert.Equal(94, contours.Max(c => c.Bounds.MaxX), 6);
        Assert.Equal(197, contours.Min(c => c.Bounds.MinY), 6); Assert.Equal(216, contours.Max(c => c.Bounds.MaxY), 6);
    }

    [Fact]
    public void NonuniformlyScaledCircleBecomesEllipseWithoutLosingTransform()
    {
        var drawing = Drawing(); var block = new BlockRecord("CIRCLE"); block.Entities.Add(new Circle { Radius = 2 }); drawing.BlockRecords.Add(block);
        drawing.Entities.Add(new Insert(block) { InsertPoint = new(10, 20, 0), XScale = 3, YScale = 2 });
        var contour = Assert.Single(Extract(drawing).Document.Contours);
        Assert.Equal(12, contour.Bounds.Width, 6); Assert.InRange(contour.Bounds.Height, 7.97, 8.0);
        Assert.InRange(contour.Area, Math.PI * 24 - 1, Math.PI * 24);
    }

    [Fact]
    public void NestedUnresolvedXrefBlocksPartialResults()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle()); var external = new BlockRecord("EXTERNAL"); external.BlockEntity.XRefPath = "missing.dwg";
        var wrapper = new BlockRecord("WRAPPER"); wrapper.Entities.Add(new Insert(external)); drawing.BlockRecords.Add(external); drawing.BlockRecords.Add(wrapper);
        drawing.Entities.Add(new Insert(wrapper));
        Assert.Contains("external-reference", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
    }

    [Fact]
    public void EllipseArcAndBothLegacyPolylineFamiliesCreateIndependentBoundaries()
    {
        var drawing = Drawing();
        drawing.Entities.Add(new Arc { Center = new(0, 0, 0), Radius = 4, StartAngle = 0, EndAngle = Math.PI });
        drawing.Entities.Add(new Line { StartPoint = new(-4, 0, 0), EndPoint = new(4, 0, 0) });
        drawing.Entities.Add(new Ellipse { Center = new(20, 0, 0), MajorAxisEndPoint = new(4, 0, 0), RadiusRatio = .5, StartParameter = 0, EndParameter = Math.Tau });
        drawing.Entities.Add(new Polyline2D(new Vertex2D[] { new() { Location = new(40, 0, 0) }, new() { Location = new(45, 0, 0) }, new() { Location = new(45, 3, 0) }, new() { Location = new(40, 3, 0) } }, true));
        drawing.Entities.Add(new Polyline3D(new XYZ[] { new(60, 0, 0), new(65, 0, 0), new(65, 3, 0), new(60, 3, 0) }, true));
        var contours = Extract(drawing).Document.Contours;
        Assert.Equal(4, contours.Length);
        Assert.Equal(2, contours.Count(c => Math.Abs(c.Area - 15) < 1e-6));
        Assert.All(contours, c => Assert.False(c.IsOuterCandidate));
        Assert.All(contours.Where(c => c.Area > 20), c => Assert.InRange(c.Area, Math.PI * 8 - .5, Math.PI * 8));
    }

    [Fact]
    public void CyclicAndTooDeepInsertsAreRejected()
    {
        var drawing = Drawing(); var cyclic = new BlockRecord("CYCLE"); cyclic.Entities.Add(new Insert(cyclic));
        // Construct before document ownership: ACadSharp clones an already-owned block in
        // Insert's constructor, and cloning a deliberately cyclic test graph never terminates.
        var cyclicRoot = new Insert(cyclic);
        drawing.BlockRecords.Add(cyclic); drawing.Entities.Add(cyclicRoot);
        Assert.Contains("Cyclic", Assert.Throws<CadImportException>(() => Extract(drawing)).Message);
        drawing = Drawing(); var leaf = new BlockRecord("LEAF"); leaf.Entities.Add(Rectangle());
        var parent = new BlockRecord("PARENT"); parent.Entities.Add(new Insert(leaf)); drawing.BlockRecords.Add(leaf); drawing.BlockRecords.Add(parent);
        drawing.Entities.Add(new Insert(parent));
        Assert.Contains("nested", Assert.Throws<CadImportException>(() => Extract(drawing, new() { Limits = new() { MaxInsertDepth = 1 } })).Message);
    }

    [Fact]
    public void RationalSplineUsesAdaptiveToleranceUnderScale()
    {
        var drawing = Drawing(); var spline = new Spline { Degree = 2, Flags = SplineFlags.Closed | SplineFlags.Rational };
        spline.ControlPoints.AddRange([new(2, 0, 0), new(2, 2, 0), new(0, 2, 0), new(-2, 2, 0), new(-2, 0, 0), new(-2, -2, 0), new(0, -2, 0), new(2, -2, 0), new(2, 0, 0)]);
        spline.Weights.AddRange(Enumerable.Range(0, 9).Select(i => i % 2 == 0 ? 1.0 : Math.Sqrt(.5)));
        spline.Knots.AddRange(new double[] { 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 4 });
        var block = new BlockRecord("SPLINE"); block.Entities.Add(spline); drawing.BlockRecords.Add(block);
        drawing.Entities.Add(new Insert(block) { XScale = 100, YScale = 100 });
        var contour = Assert.Single(Extract(drawing, new() { FlatteningTolerance = .005 }).Document.Contours);
        var maximumChordError = contour.Points.Zip(contour.Points.Skip(1), (a, b) => 200 - Math.Sqrt(Math.Pow((a.X + b.X) / 2, 2) + Math.Pow((a.Y + b.Y) / 2, 2))).Max();
        Assert.InRange(maximumChordError, 0, .0050001);
        Assert.Equal(400, contour.Bounds.Width, 6); Assert.Equal(400, contour.Bounds.Height, 6);
    }

    [Theory]
    [InlineData(false, 1.25)]
    [InlineData(true, 1.166666666666667)]
    public void FitOnlySplineInterpolatesKnownPointsAndHonorsEndpointTangentsWithoutMutatingSource(bool tangents, double expectedArea)
    {
        var drawing = Drawing(); var spline = new Spline { Degree = 2 };
        spline.FitPoints.AddRange([new(0, 0, 0), new(1, 1, 0), new(2, 0, 0)]);
        if (tangents) { spline.StartTangent = new(1, 1, 0); spline.EndTangent = new(1, -1, 0); }
        drawing.Entities.Add(spline); drawing.Entities.Add(new Line { StartPoint = new(2, 0, 0), EndPoint = new(0, 0, 0) });
        var contour = Assert.Single(Extract(drawing, new() { FlatteningTolerance = .0001, SnapTolerance = .00001 }).Document.Contours);
        Assert.InRange(contour.Area, expectedArea - .001, expectedArea + .001);
        Assert.Contains(contour.Points, p => Math.Abs(p.X - 1) < 1e-8 && Math.Abs(p.Y - 1) < 1e-8);
        Assert.Equal(2, contour.Bounds.Width, 8); Assert.Equal(1, contour.Bounds.Height, 8);
        Assert.Empty(spline.ControlPoints); Assert.Empty(spline.Knots); Assert.Equal(2, spline.Degree);
        var firstEdge = contour.Points.Where(p => p.X > .00001 && p.X < .02 && p.Y > 0).OrderBy(p => p.X).First();
        Assert.InRange(firstEdge.Y / firstEdge.X, tangents ? .99 : 1.49, tangents ? 1.02 : 1.51);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void FittedPolylineUsesStoredVertexPathAsLegacyLoaderDid(int flag)
    {
        var drawing = Drawing();
        var polyline = new Polyline2D(new Vertex2D[] { new() { Location = new(0, 0, 0) }, new() { Location = new(10, 0, 0) }, new() { Location = new(10, 5, 0) }, new() { Location = new(0, 5, 0) } }, true);
        polyline.Flags |= (PolylineFlags)flag; drawing.Entities.Add(polyline);
        Assert.Equal(50, Assert.Single(Extract(drawing).Document.Contours).Area, 8);
    }

    [Fact]
    public void ResourceLimitsStopBeforeTopologyWork()
    {
        var drawing = Drawing(); drawing.Entities.Add(Rectangle()); drawing.Entities.Add(Rectangle(20));
        Assert.Throws<CadImportException>(() => Extract(drawing, new() { Limits = new() { MaxMachiningEntities = 1 } }));
        Assert.Throws<CadImportException>(() => Extract(drawing, new() { Limits = new() { MaxClosedContours = 1 } }));
        Assert.Throws<CadImportException>(() => Extract(drawing, new() { Limits = new() { MaxEntityVertices = 3 } }));
    }

    [Fact]
    public void AnalyticUWithWideLegsImportsAndPlansWithoutFalseNarrowFeatureRejection()
    {
        // Independent public fixture: a 557 x 1348 profile with 50 mm legs and an
        // inward semicircle R228.5. It has ample material for the default 6 mm cutter.
        var drawing = Drawing();
        foreach (var (a, b) in new (XYZ, XYZ)[]
        {
            (new(0,1348,0),new(0,0,0)), (new(0,0,0),new(50,0,0)),
            (new(50,0,0),new(50,971.5,0)), (new(507,971.5,0),new(507,0,0)),
            (new(507,0,0),new(557,0,0)), (new(557,0,0),new(557,1348,0)),
            (new(557,1348,0),new(0,1348,0))
        }) drawing.Entities.Add(new Line { StartPoint = a, EndPoint = b });
        drawing.Entities.Add(new Arc { Center = new(278.5,971.5,0), Radius = 228.5, StartAngle = 0, EndAngle = Math.PI });
        var document = Extract(drawing).Document;
        var contour = Assert.Single(document.Contours);
        Assert.Equal(new Bounds2(0, 0, 557, 1348), contour.Bounds);
        var trueArea = 557 * 1348 - 457 * 971.5 - Math.PI * 228.5 * 228.5 / 2;
        Assert.InRange(contour.Area, trueArea, trueArea + Math.PI * 228.5 * .01);
        var plan = ToolpathPlanner.Generate(new(document, new(), document.DefaultSelection));
        Assert.Single(plan.Toolpaths);
    }

    [Fact]
    public void SpatialCandidateBudgetBoundsNestedOpenLinework()
    {
        var drawing = Drawing();
        for (var i = 0; i < 3; i++)
        {
            var line = new LwPolyline(); line.Vertices.AddRange([new(i, i), new(10 - i, i), new(10 - i, 10 - i), new(i, 10 - i)]); drawing.Entities.Add(line);
        }
        Assert.Contains("topology budget", Assert.Throws<CadImportException>(() => Extract(drawing, new() { Limits = new() { MaxTopologyQueryResults = 1 } })).Message);
    }

    [Fact]
    public async Task DirectDxfImportPreservesUnicodePathLayerAndSourceFingerprint()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cad2nc_import_test_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "中文轮廓.dxf");
        try
        {
            var drawing = Drawing(); var layer = new Layer("柜体"); drawing.Layers.Add(layer); drawing.Entities.Add(Rectangle(layer: layer));
            using (var writer = new DxfWriter(path, drawing, false)) writer.Write();
            var result = await new CadImporter().LoadAsync(path);
            Assert.Equal(Path.GetFullPath(path), result.Document.SourcePath); Assert.Equal(64, result.Document.SourceHash.Length);
            Assert.Equal("柜体", Assert.Single(result.Document.Contours).Layer);
            Assert.Equal(result.Document.SourceHash, new CadImporter().Load(path).Document.SourceHash);
            Assert.Throws<CadImportException>(() => new CadImporter(new() { Limits = new() { MaxDxfFileBytes = 5 } }).Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(folder); }
    }

    [Fact]
    public async Task CancellationNeverPublishesAnImportResult()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CadImporter().LoadAsync("unused.dxf", cancellation.Token));
    }

    [Fact]
    public void UnsupportedExtensionIsRejected()
    {
        Assert.Throws<CadImportException>(() => new CadImporter().Load("drawing.pdf"));
    }
}
