using System.Collections.Immutable;
using System.Text;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class LncCoreTests
{
    private static ConversionConfig Lnc() => ConfigurationDefaults.Lnc();

    private static ToolpathPlan RectanglePlan(ConversionConfig? config = null)
    {
        config ??= Lnc();
        var document = PlannerTests.Document(
            PlannerTests.Rectangle("SHEET", 100, 200, 1220, 2440, sheet: true),
            PlannerTests.Rectangle("PART", 110, 210, 100, 80));
        return ToolpathPlanner.Generate(new(document, config, document.DefaultSelection));
    }

    private static ToolpathPlan ExactRoute(Point2[] open, ConversionConfig? config = null)
    {
        var plan = RectanglePlan(config);
        var path = new Toolpath("EXACT", "CUT", open.Append(open[0]).ToImmutableArray(), 2, plan.Config.Machining.CutDirection);
        return plan with { Toolpaths = [path] };
    }

    [Fact]
    public void LncDefaultOutputIncludesConfiguredInitializationToolParkingAndTrailer()
    {
        var plan = RectanglePlan();
        var code = NcGenerator.Generate(plan);
        Assert.DoesNotContain('%', code);
        Assert.DoesNotContain("O0001", code);
        Assert.Contains("G54 G49 G90\nT2\nM03 S18000\nG43 H2\n", code);
        Assert.DoesNotContain("G21", code);
        Assert.DoesNotContain("G40", code);
        Assert.DoesNotContain("G00 G43", code);
        Assert.DoesNotContain("M06", code);
        Assert.Contains("G01 Z17.000 F8000\n", code);
        Assert.Contains("F3000\n", code);
        Assert.Contains("F10000\n", code);
        Assert.Contains("G00 X", code);
        Assert.Contains(" Z45.000 F12000\n", code);
        Assert.EndsWith("M05\nG00 Z50.000\nX200.000 Y2600.000\nM05\nM17\nM30\n", code);
        var playback = NcPlaybackParser.ParseGenerated(code, plan.Config);
        Assert.Equal(new NcPosition(200, 2600, 50), playback.Motions[^1].End);
        Assert.All(playback.Motions.Where(m => m.Kind == NcMotionKind.Rapid &&
            (m.Start.X != m.End.X || m.Start.Y != m.End.Y)), m => Assert.True(m.Start.Z >= 45 && m.End.Z >= 45));
    }

    [Fact]
    public void LncExportUsesAsciiCrLfAndOmitsFanucComments()
    {
        var output = Path.Combine(Path.GetTempPath(), "cad2nc-lnc-" + Guid.NewGuid().ToString("N") + ".nc");
        try
        {
            NcGenerator.Write(output, RectanglePlan());

            var bytes = File.ReadAllBytes(output);
            Assert.All(bytes, value => Assert.True(value < 128));
            var code = Encoding.ASCII.GetString(bytes);
            Assert.DoesNotContain('(', code);
            Assert.DoesNotContain(')', code);
            Assert.DoesNotContain('%', code);
            Assert.DoesNotContain("O0001", code);
            Assert.StartsWith("G54 G49 G90\r\nT2\r\nM03 S18000\r\nG43 H2\r\n", code);
            Assert.DoesNotContain("G21", code);
            Assert.DoesNotContain("G00 G43", code);
            Assert.Contains("G00 X", code);
            Assert.Contains(" Z45.000 F12000\r\n", code);
            Assert.Contains("G00 Z45.000 F9000\r\n", code);
            Assert.Contains("G01 X", code);
            Assert.Contains(" Z-0.100 F10000\r\n", code);
            Assert.EndsWith("M17\r\nM30\r\n", code);
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] == '\n') Assert.True(index > 0 && bytes[index - 1] == '\r');
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Fact]
    public void MachineOptionsAreIndependentOfProfileDefaults()
    {
        var config = Lnc();
        config = config with { Machine = config.Machine with { ToolChange = ToolChangeMode.ToolWithM06,
            UseProgramEnvelope = true, ParkAtEnd = false, EmitM17 = false } };
        var code = NcGenerator.Generate(RectanglePlan(config), programNumber: 123);
        Assert.StartsWith("%\nO0123\n", code);
        Assert.Contains("T2 M06\n", code);
        Assert.DoesNotContain("M17", code);
        Assert.DoesNotContain("X200.000 Y2600.000", code);
        Assert.EndsWith("M05\nM30\n%\n", code);
        Assert.NotEmpty(NcPlaybackParser.ParseGenerated(code, config).Motions);
    }

    [Fact]
    public void DepthLevelsUseStockSurfaceForExplicitLayeringOnly()
    {
        var config = Lnc();
        Assert.False(NcGenerator.UsesAutomaticSafetyLayering(config));
        Assert.Equal([-.1], NcGenerator.DepthPasses(config));
        config = config with { Machining = config.Machining with { LayeredCutting = true } };
        Assert.Equal([14d, 11, 8, 5, 2, -.1], NcGenerator.DepthPasses(config));
        config = config with { Machining = config.Machining with { BoardThickness = 18, CuttingDepth = 1 } };
        Assert.Equal([15d, 12, 9, 6, 3, 1], NcGenerator.DepthPasses(config));
        config = config with { Machining = config.Machining with { ZReference = ZReference.WorkpieceTop, CuttingDepth = -6.1 } };
        Assert.Equal([-3d, -6, -6.1], NcGenerator.DepthPasses(config));
    }

    [Fact]
    public void PositiveLeaveSkinAlwaysGeneratesSkinThenFinalPasses()
    {
        var config = Lnc() with { Machining = Lnc().Machining with { LeaveSkin = 1 } };
        var depths = NcGenerator.DepthPasses(config);

        Assert.Equal(new[] { 1d, -.1d }, depths);
        Assert.Equal(2, depths.Distinct().Count());

        var code = NcGenerator.Generate(RectanglePlan(config));
        Assert.Contains("PASS 1/2", code);
        Assert.Contains("PASS 2/2", code);
        Assert.Contains("Z1.000", code);
        Assert.Contains("Z-0.100", code);
    }

    [Fact]
    public void StableOrderCompletesAllLeaveSkinRoutesBeforeFinalRoutes()
    {
        var config = Lnc() with { Machining = Lnc().Machining with { LeaveSkin = 1, EntryMode = EntryMode.Vertical } };
        var plan = RectanglePlan(config);
        var second = plan.Toolpaths[0] with { ContourId = "SECOND" };
        var code = NcGenerator.Generate(plan with { Toolpaths = [plan.Toolpaths[0], second] });
        var passes = code.Split('\n').Where(line => line.StartsWith("(PATH ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(new[] { "(PATH 1 PASS 1/2: PART / CUT)", "(PATH 2 PASS 1/2: SECOND / CUT)",
            "(PATH 1 PASS 2/2: PART / CUT)", "(PATH 2 PASS 2/2: SECOND / CUT)" }, passes);
    }

    [Fact]
    public void LeaveSkinCannotBeNegative()
    {
        var config = Lnc() with { Machining = Lnc().Machining with { LeaveSkin = -1 } };
        var errors = config.GetValidationErrors();

        Assert.Equal("留皮厚度不能小于 0", errors[nameof(MachiningConfig.LeaveSkin)]);
        Assert.Throws<ArgumentException>(() => NcGenerator.DepthPasses(config));
    }

    [Fact]
    public void UnsafeStockHeightAndParkingAreRejectedAtSpecificFields()
    {
        var config = Lnc();
        Assert.Contains("SafeZ", (config with { Machining = config.Machining with { SafeZ = 5 } }).GetValidationErrors().Keys);
        Assert.Contains("SafeZ", (config with { Machining = config.Machining with { BoardThickness = 45 } }).GetValidationErrors().Keys);
        Assert.Contains("CuttingDepth", (config with { Machining = config.Machining with { CuttingDepth = 17 } }).GetValidationErrors().Keys);
        Assert.Contains("ParkZ", (config with { Machine = config.Machine with { ParkZ = 44 } }).GetValidationErrors().Keys);
    }

    [Fact]
    public void SheetOriginIsStableWhenSelectedPartsChangeAndAllowsNegativeCompensation()
    {
        var document = PlannerTests.Document(PlannerTests.Rectangle("SHEET", 100, 200, 500, 500, true),
            PlannerTests.Rectangle("A", 100, 200, 50, 50), PlannerTests.Rectangle("B", 300, 300, 50, 50));
        var request = new PlanningRequest(document, Lnc(), document.DefaultSelection);
        var both = ToolpathPlanner.Generate(request);
        var onlyB = ToolpathPlanner.Generate(request with { Selection = request.Selection.SetItem("A", new(false)) });
        Assert.Equal(new Point2(100, 200), both.Origin);
        Assert.Equal(both.Origin, onlyB.Origin);
        Assert.Equal(both.Toolpaths.Single(p => p.ContourId == "B").Points.ToArray(), onlyB.Toolpaths.Single().Points.ToArray());
        Assert.Equal(-2, both.Bounds.MinX);
        Assert.Equal(-2, both.Bounds.MinY);
        Assert.DoesNotContain(both.Toolpaths, path => path.ContourId == "SHEET");
        Assert.Equal(new Bounds2(101, 201, 149, 249), Bounds2.FromPoints(both.FinishedProfiles.Single(p => p.ContourId == "A").Points));
    }

    [Fact]
    public void AmbiguousAndMissingSheetsNeverFallBackToMovingRouteOrigin()
    {
        var absent = PlannerTests.Document(PlannerTests.Rectangle("PART", 0, 0, 100, 100));
        Assert.Empty(ToolpathPlanner.FindSheetCandidates(absent));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.Generate(new(absent, Lnc(), absent.DefaultSelection)));
        var ambiguous = PlannerTests.Document(PlannerTests.Rectangle("S1", 0, 0, 100, 100, true),
            PlannerTests.Rectangle("S2", 200, 0, 100, 100, true));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveSheetContour(ambiguous));
        Assert.Equal("S2", ToolpathPlanner.ResolveSheetContour(ambiguous, new() { SheetContourId = "S2" }).Id);
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveSheetContour(ambiguous, new() { SheetContourId = "STALE" }));
    }

    [Fact]
    public void ExplicitRectangleAndManualOriginsAreValidated()
    {
        var rectangle = PlannerTests.Rectangle("R", -100, 200, 50, 80);
        var rotated = new Contour("ROTATED", "CUT", PlannerTests.Polygon((0, 10), (10, 0), (20, 10), (10, 20)), true);
        var document = PlannerTests.Document(rectangle, rotated);
        Assert.True(ToolpathPlanner.IsSheetRectangle(rectangle));
        Assert.False(ToolpathPlanner.IsSheetRectangle(rotated));
        var notched = new Contour("NOTCH", "STOCK", PlannerTests.Polygon(
            (0, 0), (1000000, 0), (1000000, 1000000), (2, 1000000),
            (2, 999999.999), (1, 999999.999), (1, 1000000), (0, 1000000)), true);
        Assert.False(ToolpathPlanner.IsSheetRectangle(notched));
        Assert.Equal(rectangle, ToolpathPlanner.ResolveSheetContour(document, new() { SheetContourId = "R" }));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveSheetContour(document, new() { SheetContourId = "ROTATED" }));
        var request = new PlanningRequest(document, Lnc() with { OriginMode = OriginMode.Manual }, document.DefaultSelection,
            Workpiece: new() { ManualOrigin = new(-123.4, 321) });
        Assert.Equal(new Point2(-123.4, 321), ToolpathPlanner.ResolveFixedOrigin(request));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveFixedOrigin(request with { Workpiece = null }));
        Assert.Throws<ToolpathGenerationException>(() => ToolpathPlanner.ResolveFixedOrigin(request with { Workpiece = new() { ManualOrigin = new(double.NaN, 0) } }));
    }

    [Theory]
    [InlineData(100, 50, 80, false)]
    [InlineData(10, 10, 80, false)]
    [InlineData(10, 10, 85, false)]
    [InlineData(10, 10, 85, true)]
    [InlineData(2, 3, 81, true)]
    public void RampTraversesCornersAndMultipleLapsThenCoversEntireContourAtFinalDepth(double width, double height, double rampLength, bool clockwise)
    {
        Point2[] ring = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
        if (clockwise) Array.Reverse(ring);
        var config = Lnc();
        config = config with { Machining = config.Machining with { RampLength = rampLength,
            CutDirection = clockwise ? CutDirection.Clockwise : CutDirection.Counterclockwise,
            PrioritizePartStability = false } };
        var playback = NcPlaybackParser.ParseGenerated(NcGenerator.Generate(ExactRoute(ring, config)), config);
        var ramps = playback.Motions.Where(m => m.Kind == NcMotionKind.Linear && m.FeedRate == 3000).ToArray();
        Assert.NotEmpty(ramps);
        Assert.Equal(17, ramps[0].Start.Z);
        Assert.Equal(-.1, ramps[^1].End.Z);
        Assert.Equal(rampLength, ramps.Sum(m => double.Hypot(m.End.X - m.Start.X, m.End.Y - m.Start.Y)), 6);
        Assert.All(ramps, m => Assert.True(m.End.Z <= m.Start.Z));
        var finish = playback.Motions.Where(m => m.Kind == NcMotionKind.Linear && m.FeedRate == 10000).ToArray();
        Assert.All(finish, m => { Assert.Equal(-.1, m.Start.Z); Assert.Equal(-.1, m.End.Z); });
        Assert.Equal(2 * (width + height), finish.Sum(m => double.Hypot(m.End.X - m.Start.X, m.End.Y - m.Start.Y)), 6);
        Assert.Equal(ramps[^1].End, finish[0].Start);
        Assert.Equal(finish[0].Start, finish[^1].End);
    }

    [Fact]
    public void RampLengthDoesNotDependOnFirstSegmentOrPolylineDensity()
    {
        var open = Enumerable.Range(0, 100).Select(x => new Point2(x, 0))
            .Concat(Enumerable.Range(0, 50).Select(y => new Point2(100, y)))
            .Concat(Enumerable.Range(0, 100).Select(x => new Point2(100 - x, 50)))
            .Concat(Enumerable.Range(0, 50).Select(y => new Point2(0, 50 - y))).ToArray();
        var config = Lnc();
        config = config with { Machining = config.Machining with { PrioritizePartStability = false } };
        var plan = ExactRoute(open, config);
        var motions = NcPlaybackParser.ParseGenerated(NcGenerator.Generate(plan), plan.Config).Motions;
        var ramp = motions.Where(m => m.FeedRate == 3000).ToArray();
        Assert.Equal(80, ramp.Length);
        Assert.Equal(new NcPosition(80, 0, -.1), ramp[^1].End);
        Assert.Equal(300, motions.Where(m => m.FeedRate == 10000 && m.Kind == NcMotionKind.Linear)
            .Sum(m => double.Hypot(m.End.X - m.Start.X, m.End.Y - m.Start.Y)), 6);
    }

    [Fact]
    public void LayeredRampStartsAtSurfaceThenPreviousLayerAndCompletesEachLayer()
    {
        var config = Lnc();
        config = config with { Machining = config.Machining with { LayeredCutting = true } };
        var motions = NcPlaybackParser.ParseGenerated(NcGenerator.Generate(RectanglePlan(config)), config).Motions;
        var levels = NcGenerator.DepthPasses(config);
        for (var pass = 1; pass <= levels.Count; pass++)
        {
            var ramps = motions.Where(m => m.PassNumber == pass && m.Kind == NcMotionKind.Linear && m.FeedRate == 3000).ToArray();
            Assert.Equal(pass == 1 ? 17 : levels[pass - 2], ramps[0].Start.Z);
            Assert.Equal(levels[pass - 1], ramps[^1].End.Z);
            var finish = motions.Where(m => m.PassNumber == pass && m.Kind == NcMotionKind.Linear && m.FeedRate == 10000).ToArray();
            Assert.Equal(finish[0].Start, finish[^1].End);
            Assert.All(finish, m => Assert.Equal(levels[pass - 1], m.End.Z));
        }
    }

    [Fact]
    public void VerticalEntryRemainsAvailableWithBottomReference()
    {
        var config = Lnc();
        config = config with { Machining = config.Machining with { EntryMode = EntryMode.Vertical } };
        var code = NcGenerator.Generate(RectanglePlan(config));
        Assert.Contains("G01 Z-0.100 F8000\n", code);
        Assert.DoesNotContain("F3000", code);
        Assert.NotEmpty(NcPlaybackParser.ParseGenerated(code, config).Motions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedOrRoundingCollapsedSegmentsRejectEntireProgram(bool exactDuplicate)
    {
        var bad = ExactRoute([new(0, 0), new(exactDuplicate ? 0 : .0001, 0), new(10, 0), new(10, 10), new(0, 10)]);
        Assert.Contains("0.001", Assert.Throws<ArgumentException>(() => NcGenerator.Generate(bad)).Message);
    }

    [Fact]
    public void RampAndPositioningBlocksAreIncludedInBudgetBeforeOutput()
    {
        var config = Lnc();
        config = config with { Machining = config.Machining with { RampLength = 1000000 } };
        var plan = ExactRoute([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], config);
        Assert.Contains("500000", Assert.Throws<ArgumentException>(() => NcGenerator.Generate(plan)).Message);
    }

    [Fact]
    public void GeneratedParserAllowsOnlyConfiguredTerminalM17AndRetainsStrictAnchoring()
    {
        var config = Lnc();
        var code = NcGenerator.Generate(RectanglePlan(config));
        Assert.Throws<InvalidDataException>(() => NcPlaybackParser.Parse(code));
        Assert.Throws<InvalidDataException>(() => NcPlaybackParser.ParseGenerated(code, config with { Machine = config.Machine with { EmitM17 = false } }));
        foreach (var invalid in new[] {
            code.Replace("M17\nM30", "M17\nG00 X0\nM30"),
            code.Replace("M17\nM30", "M17\nM2"),
            code.Replace("M17\nM30", "M17 X0\nM30"),
            code.Replace("M17\nM30", "M17"),
            "G21 G90 G94\nG01 X0 Y0 F100\nM17\nM30\n",
            "G91 G28 Z0\nM17\nM30\n" })
            Assert.Throws<InvalidDataException>(() => NcPlaybackParser.ParseGenerated(invalid, config));
    }

    [Theory]
    [InlineData("lnc_060_f.nc", 135, 118)]
    [InlineData("lnc_060_z.nc", 264, 247)]
    public async Task ActualLncSamplesRemainFullyReadable(string name, int lineCount, int motionCount)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", name));
        var document = await NcDocumentReader.ReadAsync(path);
        Assert.Null(document.PlaybackError);
        Assert.Equal(lineCount, document.Lines.Count);
        Assert.Equal(motionCount, document.Program!.Motions.Length);
        Assert.Equal(new NcPosition(200, 2600, 50), document.Program.Motions[^1].End);
        Assert.Contains(document.Notices, notice => notice.Message.Contains("M17"));
        Assert.Equal(await File.ReadAllTextAsync(path), document.Code);
    }

    [Fact]
    public async Task ImportedSheetFixtureGeneratesAndStrictlyPlaysUsingDefaultLncConfig()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "lnc_sheet_mm.dxf"));
        var document = (await new CadImporter().LoadAsync(path)).Document;
        Assert.Single(ToolpathPlanner.FindSheetCandidates(document));
        var config = Lnc();
        var plan = ToolpathPlanner.Generate(new(document, config, document.DefaultSelection));
        Assert.Equal(new Point2(1000, 2000), plan.Origin);
        Assert.Equal(2, plan.Toolpaths.Length);
        var code = NcGenerator.Generate(plan);
        var playback = NcPlaybackParser.ParseGenerated(code, config);
        Assert.NotEmpty(playback.Motions);
        Assert.Equal(-.1, playback.Bounds!.MinZ);
        Assert.Equal(50, playback.Bounds.MaxZ);
    }

    [Fact]
    public void LncPhysicalExportUsesAsciiCrLfAndNoFanucComments()
    {
        var plan = RectanglePlan();
        var path = Path.Combine(Path.GetTempPath(), "lnc-format-" + Guid.NewGuid().ToString("N") + ".nc");
        try
        {
            NcGenerator.Write(path, plan);
            var bytes = File.ReadAllBytes(path);
            Assert.All(bytes, value => Assert.True(value < 128));
            var text = Encoding.ASCII.GetString(bytes);
            Assert.DoesNotContain('(', text);
            Assert.DoesNotContain(')', text);
            Assert.DoesNotContain('%', text);
            Assert.StartsWith("G54 G49 G90\r\nT2\r\nM03 S18000\r\nG43 H2\r\n", text);
            Assert.Contains("\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal));
            Assert.DoesNotContain("G00 G43", text);
            Assert.Contains("G00 X", text);
            Assert.Contains(" F12000\r\n", text);
            Assert.Contains(" F9000\r\n", text);
            Assert.Contains(" Z-0.100 F10000\r\n", text);
            Assert.EndsWith("M17\r\nM30\r\n", text);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void LncPhysicalExportRoundTripsThroughStrictGeneratedParser()
    {
        var plan = RectanglePlan();
        var path = Path.Combine(Path.GetTempPath(), "lnc-roundtrip-" + Guid.NewGuid().ToString("N") + ".nc");
        try
        {
            NcGenerator.Write(path, plan);
            var exported = Encoding.ASCII.GetString(File.ReadAllBytes(path));
            var parsed = NcPlaybackParser.ParseGenerated(exported, plan.Config);

            Assert.NotEmpty(parsed.Motions);
            Assert.Equal(new NcPosition(200, 2600, 50), parsed.Motions[^1].End);
            Assert.DoesNotContain('(', exported);
            Assert.DoesNotContain(')', exported);
            Assert.DoesNotContain('%', exported);
            Assert.DoesNotContain("G00 G43", exported);
            Assert.DoesNotContain("\n", exported.Replace("\r\n", "", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    [Fact]
    public void FailingSecondContourDoesNotOverwriteExistingNc()
    {
        var plan = RectanglePlan();
        var invalid = plan.Toolpaths[0] with { ContourId = "BAD", Points = [new(0, 0), new(.0001, 0), new(10, 10), new(0, 0)] };
        plan = plan with { Toolpaths = [plan.Toolpaths[0], invalid] };
        var path = Path.Combine(Path.GetTempPath(), "lnc-invalid-" + Guid.NewGuid().ToString("N") + ".nc");
        try
        {
            File.WriteAllText(path, "original NC");
            Assert.Throws<ArgumentException>(() => NcGenerator.Write(path, plan));
            Assert.Equal("original NC", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
