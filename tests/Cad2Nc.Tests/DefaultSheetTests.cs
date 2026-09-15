using System.Collections.Immutable;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Types.Units;
using Cad2Nc.Application;
using Cad2Nc.Core;
using DwgDocument = ACadSharp.CadDocument;

namespace Cad2Nc.Tests;

public sealed class DefaultSheetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-default-sheet-" + Guid.NewGuid().ToString("N"));
    public DefaultSheetTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static LwPolyline Rectangle(double x, double y, double width, double height)
    {
        var rectangle = new LwPolyline { IsClosed = true };
        rectangle.Vertices.AddRange([new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)]);
        return rectangle;
    }

    private static DwgDocument Drawing(params LwPolyline[] contours)
    {
        var drawing = new DwgDocument();
        drawing.Header.InsUnits = UnitsType.Millimeters;
        foreach (var contour in contours) drawing.Entities.Add(contour);
        return drawing;
    }

    private static CadDocument Extract(DwgDocument drawing) => new CadGeometryExtractor().Extract(drawing).Document;

    private string WriteDrawing(DwgDocument drawing)
    {
        var path = Path.Combine(directory, "默认母板.dxf");
        using var writer = new DxfWriter(path, drawing, false);
        writer.Write();
        return path;
    }

    [Theory]
    [InlineData(1220, 2745, 0, 0)]
    [InlineData(2745, 1220, 0, 0)]
    [InlineData(1220, 2745, 594810.125, 62408.375)]
    public void SinglePartUsesActualSheetOriginAndMatchesManualNc(double width, double height, double x, double y)
    {
        var document = Extract(Drawing(Rectangle(x, y, width, height), Rectangle(x + 20, y + 20, 348, 900)));
        var sheet = Assert.Single(ToolpathPlanner.FindSheetCandidates(document));
        Assert.False(document.DefaultSelection[sheet.Id].Included);
        var part = Assert.Single(document.Contours.Where(c => c.Id != sheet.Id));
        Assert.True(document.DefaultSelection[part.Id].Included);
        var config = ConfigurationDefaults.Lnc();
        var automatic = ToolpathPlanner.Generate(new(document, config, document.DefaultSelection));
        Assert.Equal(new Point2(x, y), automatic.Origin);
        Assert.Equal(part.Id, Assert.Single(automatic.Toolpaths).ContourId);

        // Recreate the original unmarked import and explicitly assign the sheet.
        var unmarked = document with
        {
            Contours = document.Contours.Select(c => new Contour(c.Id, c.Layer, c.Geometry, false, c.SourceEntityTypes)).ToImmutableArray()
        };
        Assert.Equal(document.Contours.Select(c => c.Fingerprint), unmarked.Contours.Select(c => c.Fingerprint));
        var manual = ToolpathPlanner.Generate(new(unmarked, config, document.DefaultSelection,
            Workpiece: new() { SheetContourId = sheet.Id }));
        Assert.Equal(NcGenerator.Generate(manual), NcGenerator.Generate(automatic));
    }

    [Theory]
    [InlineData(1220, 2745, true)]
    [InlineData(1220.01, 2744.99, true)]
    [InlineData(1219.99, 2745.01, true)]
    [InlineData(2745.01, 1219.99, true)]
    [InlineData(1220.011, 2745, false)]
    [InlineData(1220, 2744.989, false)]
    [InlineData(1200, 2400, false)]
    public void EmptySheetUsesInclusiveSizeTolerance(double width, double height, bool matches)
    {
        var document = Extract(Drawing(Rectangle(594810.125, 62408.375, width, height)));
        var contour = Assert.Single(document.Contours);
        Assert.Equal(matches, contour.IsOuterCandidate);
        Assert.Equal(!matches, document.DefaultSelection[contour.Id].Included);
        if (matches) Assert.Equal(contour.Id, ToolpathPlanner.ResolveSheetContour(document).Id);
        else Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveSheetContour(document));
    }

    [Fact]
    public void StandardBoundingBoxDoesNotMakeANotchedOutlineASheet()
    {
        var notch = new LwPolyline { IsClosed = true };
        notch.Vertices.AddRange([new(0, 0), new(1220, 0), new(1220, 2745), new(600, 2745),
            new(600, 2700), new(500, 2700), new(500, 2745), new(0, 2745)]);
        var document = Extract(Drawing(notch));
        Assert.Empty(ToolpathPlanner.FindSheetCandidates(document));
        Assert.False(Assert.Single(document.Contours).IsOuterCandidate);
    }

    [Fact]
    public void StandardSheetIsRecognizedAlongsideNonstandardHeuristicSheet()
    {
        var document = Extract(Drawing(Rectangle(0, 0, 1000, 1000), Rectangle(20, 20, 100, 100),
            Rectangle(200, 200, 100, 100), Rectangle(2000, 0, 1220, 2745)));
        Assert.Equal(new[] { 1000d, 1220d }, ToolpathPlanner.FindSheetCandidates(document).Select(c => c.Bounds.Width).Order());
        Assert.True(document.Contours.Single(c => c.Bounds.Width == 1220).IsOuterCandidate);
    }

    [Fact]
    public void StandardRectangleInsideAnotherContourIsNotASheet()
    {
        var document = Extract(Drawing(Rectangle(0, 0, 3000, 4000), Rectangle(20, 20, 1220, 2745),
            Rectangle(2000, 2000, 100, 100)));
        Assert.Equal(3000, Assert.Single(ToolpathPlanner.FindSheetCandidates(document)).Bounds.Width);
        Assert.False(document.Contours.Single(c => c.Bounds.Width == 1220).IsOuterCandidate);
    }

    [Fact]
    public async Task MixedPartCountsUseBothSheetsAndSurvivePreviewExportAndReopen()
    {
        var source = WriteDrawing(Drawing(Rectangle(100, 200, 1220, 2745), Rectangle(120, 220, 100, 100),
            Rectangle(320, 220, 100, 100), Rectangle(3000, 200, 1220, 2745), Rectangle(3020, 220, 100, 100)));
        var store = new UserStore(Path.Combine(directory, "settings"));
        using var session = new WorkbenchSession(store);
        await session.LoadCadAsync(source);
        var sheets = ToolpathPlanner.FindSheetCandidates(session.Document!);
        Assert.Equal(2, sheets.Count);
        Assert.All(sheets, s => Assert.False(session.CurrentSelection[s.Id].Included));
        Assert.Equal(3, session.Contours.Count(c => c.IsIncluded));
        await session.GenerateAsync();
        Assert.True(session.CanExport);
        Assert.True(session.IsBatchMode);
        var programs = session.BatchResult!.Programs;
        Assert.Equal(new[] { 2, 1 }, programs.Select(p => p.RouteCount));
        Assert.Equal(new[] { new Point2(100, 200), new Point2(3000, 200) }, programs.Select(p => p.Plan.Origin));
        foreach (var program in programs)
        {
            session.SelectBatchProgram(program.SheetContourId);
            Assert.True(session.CanPlay);
            Assert.Same(program.Playback, session.Playback.Program);
            Assert.NotEmpty(session.NcLines);
        }
        session.SelectBatchProgram(null);
        Assert.False(session.CanPlay);
        var paths = await session.ExportBatchNcAsync(Path.Combine(directory, "nc"));
        Assert.Equal(2, paths.Length);
        Assert.All(paths, path => Assert.NotEmpty(NcPlaybackParser.ParseGenerated(File.ReadAllText(path), session.Config).Motions));
        var taskPath = Path.Combine(directory, "mixed.cad2nc.json");
        await session.SaveTaskAsync(taskPath);
        using var reopened = new WorkbenchSession(store);
        await reopened.LoadTaskAsync(taskPath);
        Assert.False(reopened.IsDirty);
        await reopened.GenerateAsync();
        Assert.Equal(programs.Select(p => p.Code), reopened.BatchResult!.Programs.Select(p => p.Code));
    }

    [Fact]
    public void NonrectangularHeuristicCandidateDoesNotPreventSizeFallback()
    {
        var notch = new LwPolyline { IsClosed = true };
        notch.Vertices.AddRange([new(0, 0), new(1000, 0), new(1000, 1000), new(600, 1000),
            new(600, 980), new(500, 980), new(500, 1000), new(0, 1000)]);
        var document = Extract(Drawing(notch, Rectangle(20, 20, 100, 100), Rectangle(200, 200, 100, 100),
            Rectangle(2000, 0, 1220, 2745)));
        Assert.True(document.Contours.Single(c => c.Bounds.Width == 1000).IsOuterCandidate);
        Assert.Equal(1220, Assert.Single(ToolpathPlanner.FindSheetCandidates(document)).Bounds.Width);
    }

    [Fact]
    public void MultipleStandardSheetsKeepIndependentProgramsAndOrigins()
    {
        var document = Extract(Drawing(Rectangle(100, 200, 1220, 2745), Rectangle(120, 220, 100, 100),
            Rectangle(3000, 200, 2745, 1220), Rectangle(3020, 220, 100, 100)));
        var sheets = ToolpathPlanner.FindSheetCandidates(document);
        Assert.Equal(2, sheets.Count);
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveSheetContour(document));
        var batch = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());
        Assert.True(batch.Succeeded);
        Assert.Equal(2, batch.Programs.Length);
        Assert.Equal(new[] { new Point2(100, 200), new Point2(3000, 200) },
            batch.Programs.Select(p => p.Plan.Origin).OrderBy(p => p.X));
        Assert.All(batch.Programs, p =>
        {
            Assert.Single(p.Plan.Toolpaths);
            Assert.DoesNotContain(p.Plan.Toolpaths, route => sheets.Any(s => s.Id == route.ContourId));
        });
    }

    [Fact]
    public async Task ImportedDefaultSheetSurvivesTaskSaveAndSelectionChanges()
    {
        var source = WriteDrawing(Drawing(Rectangle(594810, 62408, 1220, 2745), Rectangle(594830, 62428, 348, 900)));
        var store = new UserStore(Path.Combine(directory, "settings"));
        using var session = new WorkbenchSession(store);
        await session.LoadCadAsync(source);
        var sheetId = session.Workpiece.SheetContourId;
        Assert.NotNull(sheetId);
        Assert.False(session.Contours.Single(c => c.Id == sheetId).IsIncluded);
        Assert.True(session.CanGenerate);
        await session.GenerateAsync();
        Assert.Equal(new Point2(594810, 62408), session.Plan!.Origin);
        Assert.Single(session.Plan.Toolpaths);
        var code = session.NcCode;
        var taskPath = Path.Combine(directory, "sheet.cad2nc.json");
        await session.SaveTaskAsync(taskPath);

        using var reopened = new WorkbenchSession(store);
        await reopened.LoadTaskAsync(taskPath);
        Assert.Equal(sheetId, reopened.Workpiece.SheetContourId);
        Assert.False(reopened.Contours.Single(c => c.Id == sheetId).IsIncluded);
        Assert.False(reopened.IsDirty);
        await reopened.GenerateAsync();
        Assert.Equal(code, reopened.NcCode);
        var partId = Assert.Single(reopened.Contours, c => c.Id != sheetId).Id;
        reopened.SetIncluded([partId], false);
        Assert.Equal(session.Plan.Origin, ToolpathPlanner.ResolveFixedOrigin(new(reopened.Document!, reopened.Config,
            reopened.Document!.DefaultSelection, Workpiece: reopened.Workpiece)));
        reopened.SetIncluded([partId], true);
        // Clearing a manual assignment uses the same automatic candidate.
        reopened.UpdateWorkpiece(reopened.Workpiece with { SheetContourId = null });
        await reopened.GenerateAsync();
        Assert.Equal(session.Plan.Origin, reopened.Plan!.Origin);
        Assert.Equal(code, reopened.NcCode);
    }

    [Fact]
    public async Task CommandLineUsesDefaultSheetWithoutManualAssignment()
    {
        var source = WriteDrawing(Drawing(Rectangle(100, 200, 1220, 2745), Rectangle(120, 220, 100, 100)));
        var automatic = Path.Combine(directory, "auto.nc");
        var manual = Path.Combine(directory, "manual.nc");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CommandLine.RunAsync(["--convert", source, "--output", automatic], output, error));
        var document = (await new CadImporter().LoadAsync(source)).Document;
        var sheet = ToolpathPlanner.ResolveSheetContour(document);
        Assert.Equal(0, await CommandLine.RunAsync(["--convert", source, "--output", manual, "--sheet-contour", sheet.Id], output, error));
        Assert.Equal(File.ReadAllText(manual), File.ReadAllText(automatic));
    }
}
