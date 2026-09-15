using System.Text.Json;
using Cad2Nc.Application;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class BoardLabelBuilderTests
{
    [Fact]
    public void SingleLabelUsesSheetBoundsAndExcludesStockFromPartCount()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 80, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20));
        var config = ConfigurationDefaults.Lnc();
        var plan = ToolpathPlanner.Generate(new PlanningRequest(document, config,
            document.DefaultSelection, Workpiece: new WorkpieceSetup { SheetContourId = "S1" }));

        var batch = BoardLabelBuilder.BuildSingle(document, plan, "job.nc",
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero));
        var label = Assert.Single(batch.Labels);
        Assert.Equal("S1", label.BoardId);
        Assert.Equal(new Bounds2(0, 0, 100, 80), label.BoardBounds);
        Assert.Equal("shape", label.OrderName);
        Assert.Equal(1, label.PartCount);
        Assert.Equal(plan.Toolpaths.Length, label.RouteCount);
        Assert.Contains("job.nc", label.QrPayload);
        Assert.DoesNotContain(document.SourcePath, label.QrPayload);
    }

    [Fact]
    public void BatchLabelsAreStableAndCanBeFilteredToCurrentBoard()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S2", 200, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("B", 210, 10, 20, 20),
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20));
        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());
        var when = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        var all = BoardLabelBuilder.BuildBatch(document, result, printedAtUtc: when);
        Assert.Equal(new[] { "S1", "S2" }, all.Labels.Select(label => label.BoardId));
        Assert.Equal(new[] { "01/02", "02/02" }, all.Labels.Select(label => label.Sequence));

        var current = BoardLabelBuilder.BuildBatch(document, result, "S2", when);
        var selected = Assert.Single(current.Labels);
        Assert.Equal("S2", selected.BoardId);
        using var qr = JsonDocument.Parse(selected.QrPayload);
        Assert.Equal(1, qr.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("S2", qr.RootElement.GetProperty("board").GetString());
        Assert.Equal("shape", qr.RootElement.GetProperty("order").GetString());
        Assert.Equal("2026-09-13T00:00:00.0000000+00:00", qr.RootElement.GetProperty("printedAtUtc").GetString());
    }

    [Fact]
    public void DrawingWithoutSheetUsesSingleBoardFallbackAndPlanBounds()
    {
        var document = PlannerTests.Document(PlannerTests.Rectangle("A", 10, 20, 30, 40));
        var plan = ToolpathPlanner.Generate(PlannerTests.Request(document));

        var label = Assert.Single(BoardLabelBuilder.BuildSingle(document, plan).Labels);

        Assert.Equal("单板", label.BoardId);
        Assert.Equal(plan.Bounds, label.BoardBounds);
        Assert.Equal(1, label.PartCount);
        Assert.True(label.Paths.Single().IsHighlighted);
    }
}
