using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class BatchNcGeneratorTests
{
    [Fact]
    public void SplitsContoursIntoOneProgramPerMotherboard()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20),
            PlannerTests.Rectangle("S2", 200, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("B", 210, 10, 20, 20));

        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Programs.Length);
        Assert.Equal(new[] { "S1", "S2" }, result.Programs.Select(p => p.SheetContourId));
        Assert.Equal(new[] { "A" }, result.Programs[0].Plan.Toolpaths.Select(p => p.ContourId));
        Assert.Equal(new[] { "B" }, result.Programs[1].Plan.Toolpaths.Select(p => p.ContourId));
        Assert.Equal(new Point2(0, 0), result.Programs[0].Plan.Origin);
        Assert.Equal(new Point2(200, 0), result.Programs[1].Plan.Origin);
        Assert.All(result.Programs, program =>
        {
            Assert.NotNull(program.Playback);
            Assert.Equal(program.Code, NcGenerator.Generate(program.Plan, ConfigurationDefaults.Lnc()));
            Assert.NotEmpty(program.Playback.Motions);
        });
    }

    [Fact]
    public void RejectsContourThatFallsIntoMultipleBoards()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("S2", 50, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("PART", 60, 10, 20, 20));

        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "batch.ambiguous_contour");
        Assert.Empty(result.Programs);
    }

    [Fact]
    public void KeepsMultiplePartsOnOneBoardInOneProgram()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20),
            PlannerTests.Rectangle("B", 60, 60, 20, 20));

        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());

        Assert.True(result.Succeeded);
        var program = Assert.Single(result.Programs);
        Assert.Equal(new[] { "A", "B" }, program.Plan.Toolpaths.Select(p => p.ContourId));
    }

    [Fact]
    public void PositiveLeaveSkinIsPreservedInEveryBatchProgram()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20),
            PlannerTests.Rectangle("S2", 200, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("B", 210, 10, 20, 20));
        var config = ConfigurationDefaults.Lnc() with
        {
            Machining = ConfigurationDefaults.Lnc().Machining with { LeaveSkin = 1 }
        };

        var result = BatchNcGenerator.Generate(document, config);

        Assert.True(result.Succeeded);
        Assert.All(result.Programs, program =>
        {
            Assert.Equal(new[] { 1d, -.1d }, NcGenerator.DepthPasses(program.Plan.Config));
            Assert.Contains("PASS 1/2", program.Code);
            Assert.Contains("PASS 2/2", program.Code);
        });
    }

    [Fact]
    public void WriteRefusesPartialBatchWithErrors()
    {
        var document = PlannerTests.Document(PlannerTests.Rectangle("PART", 0, 0, 10, 10));
        var result = BatchNcGenerator.Generate(document, new());

        Assert.Throws<InvalidOperationException>(() => BatchNcGenerator.Write(Path.GetTempPath(), result));
    }

    [Fact]
    public void WritesStableNumberedFilesForSuccessfulBatch()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20),
            PlannerTests.Rectangle("S2", 200, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("B", 210, 10, 20, 20));
        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc(), fileStem: "订单/测试");
        var directory = Path.Combine(Path.GetTempPath(), "cad2nc-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = BatchNcGenerator.Write(directory, result);
            Assert.Equal(new[] { "订单测试_01.nc", "订单测试_02.nc" }, paths.Select(Path.GetFileName));
            Assert.All(paths, path => Assert.True(File.Exists(path)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void WorkholdingWarningIsReportedButDoesNotBlockBatchWrite()
    {
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("S1", 0, 0, 100, 100, sheet: true),
            PlannerTests.Rectangle("A", 10, 10, 20, 20));
        var result = BatchNcGenerator.Generate(document, ConfigurationDefaults.Lnc());

        Assert.True(result.Succeeded);
        var warning = Assert.Single(result.Diagnostics, d => d.Code == "plan.workholding_unmodeled");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.False(warning.RequiresConfirmation);

        var directory = Path.Combine(Path.GetTempPath(), "cad2nc-batch-warning-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = BatchNcGenerator.Write(directory, result);
            Assert.Single(paths);
            Assert.True(File.Exists(paths[0]));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
