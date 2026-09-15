using System.Text;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class NcDocumentReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-nc-reader-" + Guid.NewGuid().ToString("N"));
    public NcDocumentReaderTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData("G3 X0 Y10 I-10 J0", 1)]
    [InlineData("G2 X0 Y-10 I-10 J0", -1)]
    public void CenterArcsKeepOneSourceBlockAndUseExactLengthAndInterpolation(string block, int direction)
    {
        var document = NcDocumentReader.Parse("G21 G17 G90 G91.1 G94 G54\nG0 X10 Y0 Z5\nF60\n" + block);
        Assert.Null(document.PlaybackError);
        var program = Assert.IsType<NcPlaybackProgram>(document.Program);
        var motion = Assert.Single(program.Motions);
        Assert.NotNull(motion.Arc);
        Assert.Equal(3, motion.LineIndex);
        Assert.Equal(direction * Math.PI / 2, motion.Arc.SweepAngle, 10);
        Assert.Equal(5 * Math.PI, motion.DurationSeconds, 10);
        var middle = program.Sample(program.DurationSeconds / 2)!;
        Assert.Equal(Math.Sqrt(50), middle.Position.X, 10);
        Assert.Equal(direction * Math.Sqrt(50), middle.Position.Y, 10);
        Assert.Equal(5, middle.Position.Z);
        Assert.Equal(motion.Start, motion.GetPreviewPoints().First());
        Assert.Equal(motion.End, motion.GetPreviewPoints().Last());
        Assert.Equal(motion.PreviewSegmentCount + 1, motion.GetPreviewPoints().Count());
    }

    [Theory]
    [InlineData("G2 X0 Y10 R10", -Math.PI / 2)]
    [InlineData("G2 X0 Y10 R-10", -3 * Math.PI / 2)]
    [InlineData("G3 X0 Y10 R10", Math.PI / 2)]
    [InlineData("G3 X0 Y10 R-10", 3 * Math.PI / 2)]
    public void RadiusSignSelectsMinorOrMajorArc(string block, double sweep)
    {
        var program = NcDocumentReader.Parse("G0 X10 Y0 Z0\nF60\n" + block).Program;
        var motion = Assert.Single(program!.Motions);
        Assert.Equal(sweep, motion.Arc!.SweepAngle, 10);
        Assert.Equal(Math.Abs(sweep) * 10, motion.DurationSeconds, 10);
        var endpoint = motion.Sample(1);
        Assert.Equal(0, endpoint.X); Assert.Equal(10, endpoint.Y);
    }

    [Fact]
    public void FullCircleAndHelixUseAnalyticBoundsAndZInterpolation()
    {
        var document = NcDocumentReader.Parse("G0 X10 Y0 Z0\nG3 I-10 J0 Z-10 F60");
        var program = document.Program!;
        var motion = Assert.Single(program.Motions);
        Assert.Equal(Math.Tau, motion.Arc!.SweepAngle);
        Assert.Equal(Math.Sqrt(Math.Pow(Math.Tau * 10, 2) + 100), motion.DurationSeconds, 10);
        var middle = motion.Sample(.5);
        Assert.Equal(-10, middle.X, 10); Assert.Equal(0, middle.Y, 10); Assert.Equal(-5, middle.Z);
        Assert.Equal(-10, program.Bounds!.MinX, 10); Assert.Equal(-10, program.Bounds.MinY, 10);
        Assert.Equal(10, program.Bounds.MaxX, 10); Assert.Equal(10, program.Bounds.MaxY, 10);
        Assert.Equal(-10, program.Bounds.MinZ); Assert.Equal(0, program.Bounds.MaxZ);
    }

    [Fact]
    public void EndpointsAndCentersHaveIndependentAbsoluteAndIncrementalModes()
    {
        var program = NcDocumentReader.Parse("G21 G17 G90 G91.1\nG0 X10 Y0 Z0\nG91 G90.1 G3 X-10 Y10 I0 J0 F60\nG90 G91.1 G3 X-10 Y0 I0 J-10").Program!;
        Assert.Equal(2, program.Motions.Length);
        Assert.Equal(new NcPosition(0, 10, 0), program.Motions[0].End);
        Assert.Equal(new NcPosition(-10, 0, 0), program.Motions[1].End);
        Assert.All(program.Motions, motion => { Assert.Equal(0, motion.Arc!.CenterX); Assert.Equal(0, motion.Arc.CenterY); Assert.Equal(Math.PI / 2, motion.Arc.SweepAngle, 10); });
    }

    [Fact]
    public void BlockUnitsApplyBeforeAxesCentersAndFeedRegardlessOfWordOrder()
    {
        var program = NcDocumentReader.Parse("G0 X1 Y0 Z0 G20\nG3 X0 Y1 I-1 J0 F1\nG91 G1 X25.4 F25.4 G21").Program!;
        Assert.Equal(new NcPosition(25.4, 0, 0), program.InitialPosition);
        Assert.Equal(25.4, program.Motions[0].Arc!.Radius, 10);
        Assert.Equal(25.4, program.Motions[0].FeedRate, 10);
        Assert.Equal(30 * Math.PI, program.Motions[0].DurationSeconds, 10);
        Assert.Equal(new NcPosition(25.4, 25.4, 0), program.Motions[1].End);
        Assert.Equal(60, program.Motions[1].DurationSeconds, 10);
    }

    [Fact]
    public void UnknownInitialPositionAndMissingZAreNotTurnedIntoMachineOriginOrDepth()
    {
        var document = NcDocumentReader.Parse("G1 X100 Y200 F60\nX110\nZ-2\nY210");
        Assert.Null(document.PlaybackError);
        Assert.Equal(new NcPosition(100, 200, 0, false), document.Program!.InitialPosition);
        Assert.Equal(3, document.Program.Motions.Length);
        Assert.Equal(10, document.Program.Motions[0].DurationSeconds);
        Assert.False(document.Program.Motions[0].Sample(.5).IsZKnown);
        Assert.Equal(0, document.Program.Motions[1].DurationSeconds);
        Assert.Equal(10, document.Program.Motions[2].DurationSeconds);
        Assert.True(document.Program.Motions[2].Start.IsZKnown);
        Assert.Equal(100, document.Program.Bounds!.MinX);
        Assert.Equal(-2, document.Program.Bounds.MinZ);
        Assert.Contains(document.Notices, notice => notice.Message.Contains("起始位置未知"));
        Assert.Contains(document.Notices, notice => notice.Message.Contains("未给出 Z"));
        Assert.Contains(document.Notices, notice => notice.Message.Contains("G21 毫米"));
    }

    [Fact]
    public void EntirelyPlanarProgramHasNoZBounds()
    {
        var program = NcDocumentReader.Parse("G0 X0 Y0\nG1 X10 F60").Program!;
        Assert.Null(program.Bounds!.MinZ); Assert.Null(program.Bounds.MaxZ);
        Assert.False(program.Sample(5)!.Position.IsZKnown);
    }

    [Theory]
    [InlineData("G91 G0 X1 Y1")]
    [InlineData("G0 X10 Y0 Z0\nG2 X0 Y10 I-10 R10 F60")]
    [InlineData("G0 X10 Y0 Z0\nG2 R10 F60")]
    [InlineData("G0 X10 Y0 Z0\nG3 X0 Y10 R1 F60")]
    [InlineData("G0 X10 Y0 Z0\nG90.1 G3 X0 Y10 I0 F60")]
    [InlineData("G0 X10 Y0 Z0\nG3 X0 Y10 I-20 J0 F60")]
    [InlineData("G0 X10 Y0 Z0\nG3 X0 Y10 I0 J0 F60")]
    [InlineData("G0 X10 Y0 Z0\nG18 G2 X0 Z10 I-10 K0 F60")]
    [InlineData("G0 X10 Y0 Z0\nG3 I-10 J0 P2 F60")]
    [InlineData("G0 X10 Y0 Z0\nG55\nG1 X20 F60")]
    [InlineData("G53 G0 X0 Y0 Z0")]
    [InlineData("G92 X0 Y0 Z0")]
    [InlineData("G10 L2 P1 X0")]
    [InlineData("G0 X10 Y0 Z0\nG81 Z-10 R2 F60")]
    [InlineData("G0 X10 Y0 Z0\nM98 P10")]
    [InlineData("G0 X10 Y0 Z0\nG1 X[#1] F60")]
    [InlineData("G0 X0 Y0 Z0\nG1 X10 F60\nG80\nX20")]
    public void UnsupportedGeometryRetainsSourceWithoutReturningPartialPlayback(string code)
    {
        var document = NcDocumentReader.Parse(code, "external.nc");
        Assert.Null(document.Program);
        Assert.NotNull(document.PlaybackError);
        Assert.Contains("NC 第", document.PlaybackError);
        Assert.Equal(code, document.Code);
        Assert.Equal("external.nc", document.SourcePath);
        Assert.Equal(code.Split('\n'), document.Lines);
    }

    [Fact]
    public void SingleWorkSystemAndNonGeometryMachineCommandsAreRecorded()
    {
        var document = NcDocumentReader.Parse("G21 G17 G90 G91.1 G94 G59\nT2 M6\nS12000 M3\nG0 G43 H2 X0 Y0 Z5\nG1 X10 F600\nM0\nM8\nM9\nM5\nM2\n%");
        Assert.Null(document.PlaybackError);
        Assert.Equal("G59", document.Program!.WorkCoordinateSystem);
        Assert.Contains(document.Parameters, parameter => parameter.Name.Contains("工件坐标系") && parameter.Value == "G59" && parameter.LineIndex == 0);
        Assert.Contains(document.Parameters, parameter => parameter.Name.StartsWith("刀号") && parameter.Value == "2" && parameter.LineIndex == 1);
        Assert.Contains(document.Notices, notice => notice.Message.Contains("不包含机床工件偏置"));
        Assert.Contains(document.Notices, notice => notice.Message.Contains("M0/M1"));
        Assert.DoesNotContain(document.Notices, notice => notice.Message.Contains("未显式指定"));
    }

    [Fact]
    public void MultipleDeclaredParametersKeepTheirOwnFirstSourceLinesWithoutGuessingToolDiameter()
    {
        var document = NcDocumentReader.Parse("O12\nT2 M6\nS12000 M3\nG0 X0 Y0 Z5\nG1 X10 F600\nF1200\nT3 M6\nS18000 M4\nG81 Z-2 R1 F60\nF600\n(tool diameter 99)");
        Assert.Null(document.Program);
        Assert.Contains(document.Parameters, value => value.Value == "12000" && value.LineIndex == 2);
        Assert.Contains(document.Parameters, value => value.Value == "18000" && value.LineIndex == 7);
        Assert.Contains(document.Parameters, value => value.Name == "进给 F" && value.Value == "60 mm/min" && value.LineIndex == 8);
        Assert.Single(document.Parameters, value => value.Name == "进给 F" && value.Value == "600 mm/min");
        Assert.DoesNotContain(document.Parameters, value => value.Name.Contains("直径") || value.Value.Contains("99"));
    }

    [Fact]
    public void UnsupportedFeedModesDoNotMislabelExtractedSourceValues()
    {
        var document = NcDocumentReader.Parse("G20 G95 F2\nG93 F3\nG96 S200");
        Assert.Null(document.Program);
        Assert.Contains(document.Parameters, value => value.Name == "进给 F" && value.Value == "2 in/rev（G95）");
        Assert.Contains(document.Parameters, value => value.Name == "进给 F" && value.Value == "3 min⁻¹（G93）");
        Assert.Contains(document.Parameters, value => value.Name.StartsWith("主轴") && value.Value == "200");
    }

    [Fact]
    public void GeneratedProgramReopensWithExactlyTheSameSourceMotions()
    {
        var config = new ConversionConfig { Machining = new() { CuttingDepth = -4, MaxStepDown = 2 } };
        var drawing = PlannerTests.Document(PlannerTests.Rectangle("PART", 10, 20, 20, 20));
        var code = NcGenerator.Generate(ToolpathPlanner.Generate(PlannerTests.Request(drawing, config)));
        var original = NcPlaybackParser.Parse(code);
        var reopened = NcDocumentReader.Parse(code);
        Assert.Null(reopened.PlaybackError);
        Assert.Equal(original.InitialPosition, reopened.Program!.InitialPosition);
        Assert.Equal(original.Motions.ToArray(), reopened.Program.Motions.ToArray());
        Assert.Equal(original.DurationSeconds, reopened.Program.DurationSeconds);
    }

    [Fact]
    public void HugeArcsStopAtDisplayBudgetWithoutAllocatingTheirPolyline()
    {
        var code = "G0 X1000000 Y0 Z0\nF60\n" + string.Concat(Enumerable.Repeat("G3 I-1000000 J0\n", 130));
        var document = NcDocumentReader.Parse(code);
        Assert.Null(document.Program);
        Assert.Contains("200 万显示点", document.PlaybackError);
        Assert.Equal(code, document.Code);
    }

    [Fact]
    public async Task ReaderKeepsUnicodePathAndUtf16OrLegacyComments()
    {
        const string code = "(中文 注释)\r\nG0 X0 Y0 Z5\r\nG1 X10 F60\r\n";
        var utf16Path = Path.Combine(directory, "中文 程序.NC");
        await File.WriteAllTextAsync(utf16Path, code, Encoding.Unicode);
        var utf16 = await NcDocumentReader.ReadAsync(utf16Path);
        Assert.Equal(code, utf16.Code); Assert.Equal(utf16Path, utf16.SourcePath);
        Assert.Single(utf16.Program!.Motions);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var legacyPath = Path.Combine(directory, "legacy.nc");
        await File.WriteAllBytesAsync(legacyPath, Encoding.GetEncoding("GB18030").GetBytes(code));
        var legacy = await NcDocumentReader.ReadAsync(legacyPath);
        Assert.Equal(code, legacy.Code); Assert.Single(legacy.Program!.Motions);
        Assert.Contains(legacy.Notices, notice => notice.Message.Contains("GB18030"));
    }

    [Fact]
    public async Task InputLimitsAndCancellationAreEnforcedBeforePublishingDocument()
    {
        var largePath = Path.Combine(directory, "large.nc");
        using (var stream = File.Create(largePath)) stream.SetLength(NcDocumentReader.MaxBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => NcDocumentReader.ReadAsync(largePath));
        Assert.Throws<InvalidDataException>(() => NcDocumentReader.Parse(new string('\n', NcDocumentReader.MaxLines + 1)));
        var path = Path.Combine(directory, "valid.nc");
        await File.WriteAllTextAsync(path, "G0 X0 Y0 Z5\nG1 X10 F60");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NcDocumentReader.ReadAsync(path, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => NcDocumentReader.Parse("G0 X0 Y0", cancellationToken: cancellation.Token));
    }
}
