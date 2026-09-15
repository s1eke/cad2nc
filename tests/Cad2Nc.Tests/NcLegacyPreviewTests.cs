using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class NcLegacyPreviewTests
{
    [Fact]
    public async Task CompleteLegacyFileKeepsSourceParametersAndAllKnownWorkMotions()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "legacy_t6_101_a18.nc"));
        var source = await File.ReadAllTextAsync(path);
        var document = await NcDocumentReader.ReadAsync(path);

        Assert.Null(document.PlaybackError);
        var program = Assert.IsType<NcPlaybackProgram>(document.Program);
        Assert.Equal(path, document.SourcePath);
        Assert.Equal(source, document.Code);
        Assert.Equal(await File.ReadAllLinesAsync(path), document.Lines);
        Assert.Equal(253, document.Lines.Count);
        Assert.Equal(243, program.Motions.Length);
        Assert.Equal(new NcPosition(715.5, 2651, 0, false), program.InitialPosition);
        Assert.DoesNotContain(program.Motions, motion => motion.LineIndex is 0 or 248 or 251);
        Assert.Equal(250, program.Motions[^1].LineIndex);
        Assert.Equal(NcMotionKind.Rapid, program.Motions[^1].Kind);
        Assert.Equal(200, program.Motions[^1].End.X);
        Assert.Equal(2600, program.Motions[^1].End.Y);
        Assert.False(program.Motions[^1].Start.IsZKnown);
        Assert.False(program.Motions[^1].End.IsZKnown);
        Assert.False(program.Sample(program.DurationSeconds)!.Position.IsZKnown);
        Assert.Equal(new NcBounds(2, 2, 1213, 2708, -.1, 38), program.Bounds);
        Assert.Contains(document.Notices, notice => notice.LineIndex == 0 && notice.Message.Contains("G28") && notice.Message.Contains("机床"));
        Assert.Contains(document.Notices, notice => notice.LineIndex == 248 && notice.Message.Contains("G28") && notice.Message.Contains("机床"));
        Assert.Contains(document.Notices, notice => notice.LineIndex == 251 && notice.Message.Contains("M17"));
        Assert.Contains(document.Parameters, parameter => parameter.Name == "刀号 T（程序指定）" && parameter.Value == "2" && parameter.LineIndex == 2);
        Assert.Contains(document.Parameters, parameter => parameter.Name == "主轴设定 S" && parameter.Value == "20000" && parameter.LineIndex == 4);
        Assert.Contains(document.Parameters, parameter => parameter.Name == "进给 F" && parameter.Value == "12000 mm/min" && parameter.LineIndex == 8);
        Assert.Contains(document.Parameters, parameter => parameter.Name == "刀长补偿号 H" && parameter.Value == "0" && parameter.LineIndex == 249);
    }

    [Fact]
    public void ReferenceReturnBeforeAnyPositionDoesNotRequireMotionModeOrInventAnOrigin()
    {
        var document = NcDocumentReader.Parse("G91 G28 Z0 M05\nG90 G0 X10 Y20 Z5\nG1 X12 F60");

        Assert.Null(document.PlaybackError);
        Assert.Equal(new NcPosition(10, 20, 5), document.Program!.InitialPosition);
        var motion = Assert.Single(document.Program.Motions);
        Assert.Equal(2, motion.LineIndex);
        Assert.Equal(new NcPosition(12, 20, 5), motion.End);
        Assert.Equal(2, motion.DurationSeconds);
    }

    [Theory]
    [InlineData("G0", NcMotionKind.Rapid)]
    [InlineData("G1", NcMotionKind.Linear)]
    public void ZReferenceReturnPreservesXYAndMotionModeButClearsDepthAndItsTravelTime(string mode, NcMotionKind kind)
    {
        var document = NcDocumentReader.Parse($"G0 X10 Y20 Z30\n{mode} X11 F60\nG91\nG28 Z0 M5\nG90 X21\nZ5\nX31");

        Assert.Null(document.PlaybackError);
        var program = document.Program!;
        Assert.Equal(4, program.Motions.Length);
        Assert.Equal(new NcPosition(10, 20, 30), program.InitialPosition);
        Assert.DoesNotContain(program.Motions, motion => motion.LineIndex == 3);
        var travel = program.Motions[1];
        Assert.Equal(4, travel.LineIndex);
        Assert.Equal(kind, travel.Kind);
        Assert.Equal(11, travel.Start.X); Assert.Equal(20, travel.Start.Y);
        Assert.Equal(21, travel.End.X); Assert.Equal(20, travel.End.Y);
        Assert.False(travel.Start.IsZKnown); Assert.False(travel.End.IsZKnown);
        Assert.False(travel.Sample(.5).IsZKnown);
        Assert.Equal(kind == NcMotionKind.Rapid ? .06 : 10, travel.DurationSeconds, 10);
        var establishDepth = program.Motions[2];
        Assert.False(establishDepth.Start.IsZKnown);
        Assert.Equal(new NcPosition(21, 20, 5), establishDepth.End);
        Assert.Equal(0, establishDepth.DurationSeconds);
        Assert.Equal(new NcPosition(21, 20, 5), program.Motions[3].Start);
        Assert.Equal(5, program.Bounds!.MinZ); Assert.Equal(30, program.Bounds.MaxZ);
    }

    [Theory]
    [InlineData("X0", "X100", 100, 20)]
    [InlineData("Y0", "Y200", 10, 200)]
    [InlineData("X0 Y0", "X100 Y200", 100, 200)]
    public void XYReferenceReturnReanchorsWithoutConnectingUnknownTravelOrReplacingInitialPosition(string axes, string anchor, double x, double y)
    {
        var document = NcDocumentReader.Parse($"G0 X10 Y20 Z30\nG1 Z29 F60\nG91 G28 {axes}\nG90 {anchor}\nG1 X{x + 10} F60");

        Assert.Null(document.PlaybackError);
        var program = document.Program!;
        Assert.Equal(new NcPosition(10, 20, 30), program.InitialPosition);
        Assert.Equal(2, program.Motions.Length);
        Assert.Equal(1, program.Motions[0].LineIndex);
        Assert.Equal(4, program.Motions[1].LineIndex);
        Assert.Equal(new NcPosition(x, y, 29), program.Motions[1].Start);
        Assert.Equal(new NcPosition(x + 10, y, 29), program.Motions[1].End);
        Assert.Equal(10, program.Motions[1].DurationSeconds);
        Assert.Equal(11, program.DurationSeconds);
    }

    [Fact]
    public void FullReferenceReturnWaitsForBothAbsoluteXYAxesAndLeavesZUnknown()
    {
        var document = NcDocumentReader.Parse("G0 X10 Y20 Z30\nG1 X11 F60\nG91 G28 X0 Y0 Z0\nG90 G0 X100\nY200\nG1 X110");

        Assert.Null(document.PlaybackError);
        var program = document.Program!;
        Assert.Equal(new NcPosition(10, 20, 30), program.InitialPosition);
        Assert.Equal(2, program.Motions.Length);
        Assert.Equal(5, program.Motions[1].LineIndex);
        Assert.Equal(new NcPosition(100, 200, 0, false), program.Motions[1].Start);
        Assert.Equal(new NcPosition(110, 200, 0, false), program.Motions[1].End);
        Assert.Equal(10, program.Motions[1].DurationSeconds);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    [InlineData("Z")]
    public void RelativeMoveOnReturnedAxisStillRequiresAWorkPosition(string axis)
    {
        var document = NcDocumentReader.Parse($"G0 X10 Y20 Z30\nG91 G28 {axis}0\nG1 {axis}1 F60");

        Assert.Null(document.Program);
        Assert.Contains("NC 第 3 行", document.PlaybackError);
        Assert.Contains(axis + " 起始位置未知", document.PlaybackError);
    }

    [Fact]
    public void ClearingAllAxesDoesNotPermitAWorkCoordinateSystemChangeAfterPositioning()
    {
        var document = NcDocumentReader.Parse("G54 G0 X10 Y20 Z30\nG91 G28 X0 Y0 Z0\nG55\nG90 G0 X100 Y200 Z5");

        Assert.Null(document.Program);
        Assert.Contains("NC 第 3 行", document.PlaybackError);
        Assert.Contains("工件坐标系", document.PlaybackError);
    }

    [Theory]
    [InlineData("G90 G28 Z0")]
    [InlineData("G91 G28 Z1")]
    [InlineData("G91 G28 X-1 Z0")]
    [InlineData("G91 G28")]
    [InlineData("G91 G28 M5")]
    [InlineData("G91 G28 Z0 G0")]
    [InlineData("G91 G28 Z0 G1")]
    [InlineData("G91 G28 Z0 G2")]
    [InlineData("G91 G28 Z0 G3")]
    [InlineData("G91 G28 Z0 G80")]
    [InlineData("G91 G28 Z0 I0")]
    [InlineData("G91 G28 Z0 J0")]
    [InlineData("G91 G28 Z0 R1")]
    [InlineData("G91 G28 G28 Z0")]
    [InlineData("G91 G28 Z0 Z0")]
    public void AmbiguousReferenceReturnsRejectTheWholePreviewAndKeepTheSource(string block)
    {
        var code = "G0 X10 Y20 Z30\nG1 X11 F60\n" + block;
        var document = NcDocumentReader.Parse(code, "ambiguous.nc");

        Assert.Null(document.Program);
        Assert.Contains("NC 第 3 行", document.PlaybackError);
        Assert.Equal(code, document.Code);
        Assert.Equal("ambiguous.nc", document.SourcePath);
    }

    [Theory]
    [InlineData("M17\nM30")]
    [InlineData("M17\nM2")]
    [InlineData("N170 M17 (machine footer)\n\n; footer comment\nN180 M30\n%\n(end)\n")]
    [InlineData("N170M17 ; machine footer\n(comment)\nN180M2\n\n%\n")]
    [InlineData("M17\n%\nM30\n%")]
    public void StandaloneM17BeforeStandaloneProgramEndPreservesSourceLinesAndOnlyAddsANotice(string footer)
    {
        var code = "G0 X10 Y20 Z5\nG1 X20 F60\n" + footer;
        var document = NcDocumentReader.Parse(code);

        Assert.Null(document.PlaybackError);
        var motion = Assert.Single(document.Program!.Motions);
        Assert.Equal(1, motion.LineIndex);
        Assert.Equal(new NcPosition(20, 20, 5), motion.End);
        Assert.Equal(10, motion.DurationSeconds);
        Assert.Contains(document.Notices, notice => notice.LineIndex == 2 && notice.Message.Contains("M17"));
        Assert.Equal(code, document.Code);
    }

    [Theory]
    [InlineData("M17")]
    [InlineData("M17\n(comment)\n%")]
    [InlineData("M17\nG0 X30\nM30")]
    [InlineData("M17\nM5\nM30")]
    [InlineData("M17\nM17\nM30")]
    [InlineData("M17 M17\nM30")]
    [InlineData("M17 N-1\nM30")]
    [InlineData("N1 M17 N2\nM30")]
    [InlineData("M17 M30")]
    [InlineData("M17 M5\nM30")]
    [InlineData("M17 X30\nM30")]
    [InlineData("M17 G90\nM30")]
    [InlineData("M17 F120\nM30")]
    [InlineData("M17 O100\nM30")]
    [InlineData("M17\nM30 X30")]
    [InlineData("M17\nM2 M5")]
    [InlineData("M17\nM30 N-1")]
    [InlineData("M17\nM30 N1.5")]
    [InlineData("M17\nN1 M30 N2")]
    [InlineData("M17\nM30\nG0 X30")]
    [InlineData("M17\nM30\nO100")]
    public void NonterminalOrMixedM17NeverReturnsAPartialPreview(string footer)
    {
        var code = "G0 X10 Y20 Z5\nG1 X20 F60\n" + footer;
        var document = NcDocumentReader.Parse(code);

        Assert.Null(document.Program);
        Assert.Contains("NC 第", document.PlaybackError);
        Assert.Equal(code, document.Code);
    }

    [Theory]
    [InlineData("G29")]
    [InlineData("G53 G0 X0")]
    [InlineData("G81 Z-1 R1 F60")]
    [InlineData("M18")]
    [InlineData("M98 P1")]
    public void LegacyCompatibilityDoesNotMakeOtherUnsupportedInstructionsIgnorable(string block)
    {
        var document = NcDocumentReader.Parse("G0 X10 Y20 Z5\nG1 X20 F60\n" + block);

        Assert.Null(document.Program);
        Assert.Contains("NC 第 3 行", document.PlaybackError);
    }

    [Theory]
    [InlineData("G91 G28 Z0\nG90 G0 X10 Y20 Z5\nG1 X20 F60")]
    [InlineData("G0 X10 Y20 Z5\nG1 X20 F60\nM17\nM30")]
    public void GeneratedProgramParserKeepsRejectingTheExternalMachineCompatibilityExceptions(string code)
    {
        Assert.Throws<InvalidDataException>(() => NcPlaybackParser.Parse(code));
    }
}
