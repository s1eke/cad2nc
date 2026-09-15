using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class NcTests
{
    private static ToolpathPlan Plan(ConversionConfig? config = null)
    {
        var contour = new Contour("C1", "中文切割层", PlannerTests.Polygon((0, 0), (10, 0), (10, 5), (0, 5)));
        return ToolpathPlanner.Generate(PlannerTests.Request(PlannerTests.Document(contour), config));
    }

    [Fact]
    public void DefaultOutputUsesMetricAbsoluteFanucAndSoftwareCompensation()
    {
        var code = NcGenerator.Generate(Plan());
        Assert.StartsWith("%\nO0001\n", code);
        Assert.Contains("G21 G17 G90 G94\n", code);
        Assert.Contains("G40 G49 G80\n", code);
        Assert.Contains("G54\nM05\nT2 M06\n", code);
        Assert.Contains("G00 G43 H2 Z5.000\n", code);
        Assert.Contains("S18000 M03\n", code);
        Assert.Contains("G01 Z-0.100 F1000\n", code);
        Assert.Contains("F10000\n", code);
        Assert.DoesNotContain("G41", code);
        Assert.DoesNotContain("G42", code);
        Assert.EndsWith("M05\nG49\nM30\n%\n", code);
        Assert.DoesNotContain('\r', code);
        Assert.All(code, c => Assert.True(c < 128));
        Assert.All(code.Split('\n'), line => Assert.True(line.Length <= NcGenerator.MaxBlockLength));
    }

    [Fact]
    public void DeepCutSplitsIntoExactDepthsWithRetractsBeforeAndAfterEveryPass()
    {
        var config = new ConversionConfig { Machining = new() { CuttingDepth = -18.5, MaxStepDown = 3 } };
        var code = NcGenerator.Generate(Plan(config));
        var depths = Regex.Matches(code, @"G01 Z(-?\d+\.\d{3}) F1000")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal([-3d, -6d, -9d, -12d, -15d, -18d, -18.5d], depths);
        Assert.Equal(depths.Length * 2, Regex.Matches(code, @"G00 Z5\.000").Count);
    }

    [Theory]
    [InlineData(-6, 3, 2)]
    [InlineData(-0.3, 0.1, 3)]
    [InlineData(-18.5, 3, 7)]
    [InlineData(-0.001, 3, 1)]
    public void DepthPassesHaveNoDuplicateFinalLayer(double finalDepth, double step, int count)
    {
        var depths = NcGenerator.DepthPasses(new ConversionConfig { Machining = new() { CuttingDepth = finalDepth, MaxStepDown = step } });
        Assert.Equal(count, depths.Count);
        Assert.Equal(finalDepth, depths[^1]);
        Assert.Equal(count, depths.Distinct().Count());
        Assert.All(depths, depth => Assert.True(depth < 0 && depth >= finalDepth));
    }

    [Fact]
    public void SpindleDirectionProgramToolAndOffsetsAreTakenFromConfig()
    {
        var config = new ConversionConfig
        {
            Tool = new() { ToolNumber = 7, LengthOffset = 8, SpindleDirection = SpindleDirection.Counterclockwise },
            Machining = new() { SpindleSpeed = 12000, PlungeFeed = 125.125, CuttingFeed = 500.5 },
        };
        var code = NcGenerator.Generate(Plan(config), programNumber: 42);
        Assert.Contains("O0042\n", code);
        Assert.Contains("T7 M06\nS12000 M04\nG00 G43 H8 Z5.000", code);
        Assert.Contains("F125.125", code);
        Assert.Contains("F500.5", code);
    }

    [Fact]
    public void OutputNeverUsesCurrentCultureDecimalSeparators()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var code = NcGenerator.Generate(Plan(new() { Machining = new() { CuttingFeed = 1234.5 } }));
            Assert.Contains("F1234.5", code);
            Assert.Contains("Z-0.100", code);
            Assert.DoesNotContain("1234,5", code);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void EmptyPlanCannotGenerateCode()
    {
        var plan = Plan() with { Toolpaths = [] };
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void InvalidProgramNumberIsRejected(int number) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NcGenerator.Generate(Plan(), programNumber: number));

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(-1, double.PositiveInfinity)]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    [InlineData(-10, 0.001)]
    public void UnsafeOrExcessiveDepthPassesAreRejectedBeforeAllocation(double depth, double step) =>
        Assert.Throws<ArgumentException>(() => NcGenerator.DepthPasses(new ConversionConfig { Machining = new() { CuttingDepth = depth, MaxStepDown = step } }));

    [Fact]
    public void MotionBudgetIsCheckedBeforeAllocatingNC()
    {
        var config = new ConversionConfig { Machining = new() { CuttingDepth = -1, MaxStepDown = 0.001 } };
        var points = Enumerable.Range(0, 601).Select(i => new Point2(i % 2, i % 3)).Append(new(0, 0)).ToImmutableArray();
        var route = new Toolpath("MANY", "CUT", points, 2, CutDirection.Counterclockwise);
        var plan = Plan(config) with { Toolpaths = [route] };
        var error = Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan));
        Assert.Contains("500000", error.Message);
    }

    [Fact]
    public void MetadataCannotInjectBlocksOrCloseAComment()
    {
        var plan = Plan();
        var route = plan.Toolpaths[0] with { ContourId = "C1\nM30\n%", Layer = "L1\r\nG00 Z-999\0） G01 X0 （" };
        var code = NcGenerator.Generate(plan with { Toolpaths = [route] });
        Assert.Single(Regex.Matches(code, "\nM30\n"));
        Assert.DoesNotContain("\nG00 Z-999", code);
        var comment = Assert.Single(code.Split('\n'), line => line.StartsWith("(PATH"));
        Assert.Equal(1, comment.Count(c => c == '('));
        Assert.Equal(1, comment.Count(c => c == ')'));
        Assert.EndsWith(")", comment);
        Assert.DoesNotContain('%', comment);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2000000)]
    public void NonfiniteOrOversizedCoordinatesAreRejected(double value)
    {
        var plan = Plan();
        var route = new Toolpath("BAD", "CUT", [new(0, 0), new(value, 0), new(0, 10), new(0, 0)], 0, CutDirection.Counterclockwise);
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan with { Toolpaths = [route] }));
    }

    [Fact]
    public void ForgedOpenRouteCannotBeEmitted()
    {
        var plan = Plan();
        var route = plan.Toolpaths[0] with { Points = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)] };
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan with { Toolpaths = [route] }));
    }

    [Fact]
    public void APlanCannotBeUsedWithDifferentGeometricParameters()
    {
        var plan = Plan();
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan, new() { Tool = new() { Diameter = 10 } }));
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan, new() { Machining = new() { EdgeAllowance = 2 } }));
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan, new() { Machining = new() { CutDirection = CutDirection.Clockwise } }));
    }

    [Fact]
    public void UnsafeFeedIsRejected()
    {
        var plan = Plan();
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan, new() { Machining = new() { CuttingFeed = 0.0001 } }));
        Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan, new() { Machining = new() { PlungeFeed = double.NaN } }));
    }

    [Fact]
    public void ExportIsCompleteAsciiAndSupportsUnicodeFilePaths()
    {
        using var folder = new TemporaryFolder();
        var output = Path.Combine(folder.Path, "刀路.nc");
        var plan = Plan();
        Assert.Equal(Path.GetFullPath(output), NcGenerator.Write(output, plan));
        var bytes = File.ReadAllBytes(output);
        Assert.All(bytes, value => Assert.True(value < 128));
        Assert.Equal(NcGenerator.Generate(plan), Encoding.ASCII.GetString(bytes));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public void InvalidPlanCannotOverwriteExistingOutput()
    {
        using var folder = new TemporaryFolder();
        var output = Path.Combine(folder.Path, "preserve.nc");
        File.WriteAllText(output, "existing complete program");
        Assert.Throws<ArgumentException>(() => NcGenerator.Write(output, Plan() with { Toolpaths = [] }));
        Assert.Equal("existing complete program", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("source.DWG")]
    [InlineData("source.dxf")]
    public void ExportCannotOverwriteCAD(string filename)
    {
        using var folder = new TemporaryFolder();
        var output = Path.Combine(folder.Path, filename);
        File.WriteAllText(output, "CAD input");
        Assert.Throws<ArgumentException>(() => NcGenerator.Write(output, Plan()));
        Assert.Equal("CAD input", File.ReadAllText(output));
    }

    [Fact]
    public void ExportCannotOverwriteAnExtensionlessSource()
    {
        using var folder = new TemporaryFolder();
        var source = Path.Combine(folder.Path, "source");
        File.WriteAllText(source, "CAD input");
        var plan = Plan();
        plan = plan with { Request = plan.Request with { Document = plan.Request.Document with { SourcePath = source } } };
        Assert.Throws<ArgumentException>(() => NcGenerator.Write(source, plan));
        Assert.Equal("CAD input", File.ReadAllText(source));
    }

    [Fact]
    public void AtomicWriteFailurePreservesOldFileAndRemovesItsTemporaryFile()
    {
        using var folder = new TemporaryFolder();
        var output = Path.Combine(folder.Path, "existing.txt");
        File.WriteAllText(output, "original");
        var strictAscii = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Assert.Throws<EncoderFallbackException>(() => AtomicFile.WriteText(output, "中文", strictAscii));
        Assert.Equal("original", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
        AtomicFile.WriteText(output, "replacement", strictAscii);
        Assert.Equal("replacement", File.ReadAllText(output));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cad2nc-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
