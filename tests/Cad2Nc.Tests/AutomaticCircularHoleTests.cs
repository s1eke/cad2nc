using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using Cad2Nc.Application;
using Cad2Nc.Core;
using DwgDocument = ACadSharp.CadDocument;

namespace Cad2Nc.Tests;

public sealed class AutomaticCircularHoleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-circular-holes-" + Guid.NewGuid().ToString("N"));

    public AutomaticCircularHoleTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static LwPolyline Polygon(params (double X, double Y)[] points)
    {
        var polygon = new LwPolyline { IsClosed = true };
        foreach (var point in points) polygon.Vertices.Add(new(point.X, point.Y));
        return polygon;
    }

    private static LwPolyline Rectangle(double x, double y, double width, double height) =>
        Polygon((x, y), (x + width, y), (x + width, y + height), (x, y + height));

    private static DwgDocument Drawing(bool sheet = true, bool part = true)
    {
        var drawing = new DwgDocument();
        drawing.Header.InsUnits = UnitsType.Millimeters;
        if (sheet) drawing.Entities.Add(Rectangle(0, 0, 1220, 2745));
        if (part) drawing.Entities.Add(Rectangle(100, 100, 800, 1600));
        return drawing;
    }

    private static DwgDocument DrawingWithTwoHoles()
    {
        var drawing = Drawing();
        drawing.Entities.Add(new Circle { Center = new(250, 400, 0), Radius = 70 });
        drawing.Entities.Add(new Circle { Center = new(600, 1000, 0), Radius = 30 });
        return drawing;
    }

    private static CadDocument Extract(DwgDocument drawing) => new CadGeometryExtractor().Extract(drawing).Document;

    private static Contour At(CadDocument document, double minX, double minY) => Assert.Single(document.Contours,
        contour => Math.Abs(contour.Bounds.MinX - minX) < 1e-6 && Math.Abs(contour.Bounds.MinY - minY) < 1e-6);

    private string WriteDrawing(DwgDocument drawing)
    {
        var path = Path.Combine(directory, "内部圆孔.dxf");
        using var writer = new DxfWriter(path, drawing, false);
        writer.Write();
        return path;
    }

    [Fact]
    public async Task FreshImportGeneratesBothCircularHolesWithoutManualSideChanges()
    {
        var source = WriteDrawing(DrawingWithTwoHoles());
        using var session = new WorkbenchSession(new UserStore(Path.Combine(directory, "settings")));
        await session.LoadCadAsync(source);

        var document = session.Document!;
        Assert.Equal(4, document.Contours.Length);
        var sheet = Assert.Single(ToolpathPlanner.FindSheetCandidates(document));
        var part = At(document, 100, 100);
        var holes = new[] { At(document, 180, 330), At(document, 570, 970) };
        Assert.False(session.CurrentSelection[sheet.Id].Included);
        Assert.Equal(CutSide.Outside, session.CurrentSelection[part.Id].Side);
        Assert.All(holes, hole =>
        {
            Assert.True(session.CurrentSelection[hole.Id].Included);
            Assert.Equal(CutSide.Inside, session.CurrentSelection[hole.Id].Side);
        });

        await session.GenerateAsync();

        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);
        Assert.Equal(3, session.Plan!.Toolpaths.Length);
        Assert.True(Assert.Single(session.Plan.Toolpaths, path => path.ContourId == part.Id).OffsetDistance > 0);
        Assert.All(holes, hole => Assert.True(Assert.Single(session.Plan.Toolpaths, path => path.ContourId == hole.Id).OffsetDistance < 0));
        Assert.True(session.CanExport);
        Assert.True(session.CanPlay);
        Assert.False(string.IsNullOrWhiteSpace(session.NcCode));
        Assert.NotNull(session.Playback.Program);
    }

    [Fact]
    public void OtherShapesOnTheSameLayerAsACircleKeepOutsideDefaults()
    {
        var drawing = Drawing();
        drawing.Entities.Add(new Circle { Center = new(250, 400, 0), Radius = 50 });
        drawing.Entities.Add(Rectangle(450, 350, 100, 100));
        // Even a circular-looking polyline is not evidence of a source CIRCLE.
        drawing.Entities.Add(Polygon(Enumerable.Range(0, 64).Select(index =>
            (700 + 50 * Math.Cos(index * Math.Tau / 64), 400 + 50 * Math.Sin(index * Math.Tau / 64))).ToArray()));
        var document = Extract(drawing);

        Assert.Single(document.Contours.Select(contour => contour.Layer).Distinct());
        Assert.Equal(CutSide.Inside, document.DefaultSelection[At(document, 200, 350).Id].Side);
        Assert.Equal(CutSide.Outside, document.DefaultSelection[At(document, 450, 350).Id].Side);
        Assert.Equal(CutSide.Outside, document.DefaultSelection[At(document, 650, 350).Id].Side);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundPartsDirectlyOnStockOrWithoutAHostRemainOutside(bool sheet)
    {
        var drawing = Drawing(sheet: sheet, part: false);
        drawing.Entities.Add(new Circle { Center = new(250, 400, 0), Radius = 50 });
        var document = Extract(drawing);
        var roundPart = At(document, 200, 350);

        Assert.True(document.DefaultSelection[roundPart.Id].Included);
        Assert.Equal(CutSide.Outside, document.DefaultSelection[roundPart.Id].Side);
    }

    [Fact]
    public void CircleInAConcaveOutlineBoundingBoxButOutsideItsMaterialRemainsOutside()
    {
        var drawing = Drawing(part: false);
        drawing.Entities.Add(Polygon((100, 100), (900, 100), (900, 400), (400, 400), (400, 1700), (100, 1700)));
        drawing.Entities.Add(new Circle { Center = new(600, 800, 0), Radius = 50 });
        var document = Extract(drawing);

        Assert.Equal(CutSide.Outside, document.DefaultSelection[At(document, 550, 750).Id].Side);
    }

    [Fact]
    public void CircleTouchingItsPartBoundaryOnAnotherLayerIsNotAutomaticallyAHole()
    {
        var drawing = Drawing();
        var holeLayer = new Layer("HOLE");
        drawing.Layers.Add(holeLayer);
        drawing.Entities.Add(new Circle { Center = new(150, 400, 0), Radius = 50, Layer = holeLayer });
        var document = Extract(drawing);

        Assert.Equal(CutSide.Outside, document.DefaultSelection[At(document, 100, 350).Id].Side);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmbiguousConcentricOrDeeperNestedCirclesKeepOutsideDefaults(bool concentric)
    {
        var drawing = Drawing();
        if (concentric)
            drawing.Entities.Add(new Circle { Center = new(300, 400, 0), Radius = 80 });
        else
            drawing.Entities.Add(Rectangle(200, 300, 200, 200));
        drawing.Entities.Add(new Circle { Center = new(300, 400, 0), Radius = 30 });
        var document = Extract(drawing);

        Assert.All(document.DefaultSelection.Values, selection => Assert.Equal(CutSide.Outside, selection.Side));
    }

    [Fact]
    public void SimpleCircularRingHasAnOutsidePartAndAnInsideHole()
    {
        var drawing = Drawing(part: false);
        drawing.Entities.Add(new Circle { Center = new(300, 400, 0), Radius = 80 });
        drawing.Entities.Add(new Circle { Center = new(300, 400, 0), Radius = 30 });
        var document = Extract(drawing);

        Assert.Equal(CutSide.Outside, document.DefaultSelection[At(document, 220, 320).Id].Side);
        Assert.Equal(CutSide.Inside, document.DefaultSelection[At(document, 270, 370).Id].Side);
        var plan = ToolpathPlanner.Generate(new(document, ConfigurationDefaults.Lnc(), document.DefaultSelection));
        Assert.Equal(2, plan.Toolpaths.Length);
    }

    [Theory]
    [InlineData(3, 3, CutSide.Inside)]
    [InlineData(-3, 3, CutSide.Inside)]
    [InlineData(3, 2, CutSide.Outside)]
    public void BlockTransformsRecognizeUniformAndMirroredCirclesButNotEllipses(double xScale, double yScale, CutSide expected)
    {
        var drawing = Drawing();
        var block = new BlockRecord("ROUND_HOLE");
        block.Entities.Add(new Circle { Radius = 20 });
        drawing.BlockRecords.Add(block);
        drawing.Entities.Add(new Insert(block)
        {
            InsertPoint = new(300, 400, 0), XScale = xScale, YScale = yScale, Rotation = .7
        });
        var document = Extract(drawing);
        var transformed = Assert.Single(document.Contours, contour => !contour.IsOuterCandidate && contour.Area < 100_000);

        Assert.Equal(expected, document.DefaultSelection[transformed.Id].Side);
    }

    [Fact]
    public async Task SavedExplicitChoicesOverrideDefaultsButFreshCadImportRecomputesThem()
    {
        var source = WriteDrawing(DrawingWithTwoHoles());
        var store = new UserStore(Path.Combine(directory, "settings"));
        using var session = new WorkbenchSession(store);
        await session.LoadCadAsync(source);
        var hole = At(session.Document!, 180, 330);
        var otherHole = At(session.Document!, 570, 970);
        Assert.Equal(CutSide.Inside, session.CurrentSelection[hole.Id].Side);
        session.SetCutSide([hole.Id], CutSide.Outside);
        session.SetIncluded([otherHole.Id], false);
        var taskPath = Path.Combine(directory, "手动选择.cad2nc.json");
        await session.SaveTaskAsync(taskPath);

        using var reopened = new WorkbenchSession(store);
        await reopened.LoadTaskAsync(taskPath);
        Assert.Equal(CutSide.Outside, reopened.CurrentSelection[hole.Id].Side);
        Assert.False(reopened.CurrentSelection[otherHole.Id].Included);
        Assert.Equal(CutSide.Inside, reopened.CurrentSelection[otherHole.Id].Side);
        Assert.False(reopened.IsDirty);

        using var fresh = new WorkbenchSession(store);
        await fresh.LoadCadAsync(source);
        Assert.All(new[] { hole, otherHole }, contour =>
        {
            Assert.True(fresh.CurrentSelection[contour.Id].Included);
            Assert.Equal(CutSide.Inside, fresh.CurrentSelection[contour.Id].Side);
        });
    }
}
