using System.Globalization;
using Cad2Nc.Application;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class NcPlaybackTests
{
    private const string SimpleProgram = "G21 G17 G90 G94\nG00 Z5\nG00 X10 Y20\n(PATH 1 PASS 2/3: CUT / LAYER)\nG01 Z-1 F60\nX13 Y24 F300\nG00 Z5\nM30\n";

    [Fact]
    public void ParsesActualModalCoordinatesFeedsAndSourceLines()
    {
        var program = NcPlaybackParser.Parse(SimpleProgram);
        Assert.Equal(new NcPosition(10, 20, 5), program.InitialPosition);
        Assert.Equal(3, program.Motions.Length);
        var plunge = program.Motions[0];
        Assert.Equal(new NcPosition(10, 20, -1), plunge.End);
        Assert.Equal(NcMotionKind.Linear, plunge.Kind);
        Assert.Equal(60, plunge.FeedRate);
        Assert.Equal(4, plunge.LineIndex);
        Assert.Equal(1, plunge.PathNumber); Assert.Equal(2, plunge.PassNumber); Assert.Equal(3, plunge.PassCount);
        Assert.Equal("CUT / LAYER", plunge.PathLabel);
        Assert.Equal(1, plunge.OperationNumber); Assert.Equal(1, plunge.OperationCount);
        Assert.Equal(6, plunge.DurationSeconds, 10);
        var cut = program.Motions[1];
        Assert.Equal(plunge.End, cut.Start); Assert.Equal(new NcPosition(13, 24, -1), cut.End);
        Assert.Equal(300, cut.FeedRate); Assert.Equal(1, cut.DurationSeconds, 10);
        var retract = program.Motions[2];
        Assert.Equal(NcMotionKind.Rapid, retract.Kind);
        Assert.Equal(NcPlaybackParser.RapidFeedRate, retract.FeedRate);
        Assert.Equal(7.036, program.DurationSeconds, 10);
    }

    [Fact]
    public void DoesNotInventTravelFromUnknownMachineOrigin()
    {
        var program = NcPlaybackParser.Parse("G00 Z50\nG00 X100\nY200\nG1 Z-2 F100\n");
        Assert.Equal(new NcPosition(100, 200, 50), program.InitialPosition);
        var motion = Assert.Single(program.Motions);
        Assert.Equal(program.InitialPosition, motion.Start);
        Assert.Equal(3, motion.LineIndex);
        Assert.Equal(31.2, motion.DurationSeconds, 10);
    }

    [Fact]
    public void SamplesLinearInterpolationBoundariesAndFinalPosition()
    {
        var program = NcPlaybackParser.Parse(SimpleProgram);
        Assert.Equal(new NcPosition(10, 20, 2), program.Sample(3)!.Position);
        var boundary = program.Sample(6)!;
        Assert.Equal(1, boundary.MotionIndex); Assert.Equal(0, boundary.MotionProgress);
        Assert.Equal(new NcPosition(11.5, 22, -1), program.Sample(6.5)!.Position);
        Assert.Equal(program.Motions[0].Start, program.Sample(-100)!.Position);
        var end = program.Sample(double.MaxValue)!;
        Assert.Equal(2, end.MotionIndex); Assert.Equal(1, end.MotionProgress);
        Assert.Equal(new NcPosition(13, 24, 5), end.Position);
    }

    [Fact]
    public void ZeroLengthMotionsDoNotTrapPlaybackAtAnInstantaneousBlock()
    {
        var program = NcPlaybackParser.Parse("G0X0Y0Z5\nG0Z5\nG1X10F60\nX10\nG0Z5\n");
        Assert.Equal(4, program.Motions.Length);
        Assert.Equal(1, program.Sample(0)!.MotionIndex);
        Assert.Equal(3, program.Sample(program.DurationSeconds)!.MotionIndex);
        Assert.Equal(new NcPosition(10, 0, 5), program.Sample(program.DurationSeconds)!.Position);
        Assert.Equal(10, program.DurationSeconds);
    }

    [Fact]
    public void ModalFeedSurvivesRapidAndSeveralPartialAxisBlocks()
    {
        var program = NcPlaybackParser.Parse("G0X0Y0Z5\nG1Z-1F60\nX2\nG0Z5\nG1Z-1\nY3\n");
        Assert.All(program.Motions.Where(m => m.Kind == NcMotionKind.Linear), m => Assert.Equal(60, m.FeedRate));
        Assert.Equal(new NcPosition(2, 3, -1), program.Motions[^1].End);
        Assert.Equal(3, program.Motions[^1].DurationSeconds);
    }

    [Fact]
    public void CurrentGeneratedProgramContainsEveryDepthAndTransition()
    {
        var config = new ConversionConfig { Machining = new() { CuttingDepth = -4.5, MaxStepDown = 2 } };
        var document = PlannerTests.Document(PlannerTests.Rectangle("LEFT", 10, 20, 20, 20), PlannerTests.Rectangle("RIGHT", 100, 20, 20, 20));
        var plan = ToolpathPlanner.Generate(PlannerTests.Request(document, config));
        var code = NcGenerator.Generate(plan);
        var program = NcPlaybackParser.Parse(code);
        var lines = code.Split('\n');
        Assert.Equal(new NcPosition(plan.Toolpaths[0].Points[0].X, plan.Toolpaths[0].Points[0].Y, config.Machining.SafeZ), program.InitialPosition);
        var plunges = program.Motions.Where(m => m.Kind == NcMotionKind.Linear && m.End.Z < m.Start.Z).ToArray();
        Assert.Equal(new[] { -2d, -2d, -4d, -4d, -4.5d, -4.5d }, plunges.Select(m => m.End.Z));
        Assert.Equal(new[] { 1, 2, 1, 2, 1, 2 }, plunges.Select(m => m.PathNumber));
        Assert.Equal(new[] { 1, 1, 2, 2, 3, 3 }, plunges.Select(m => m.PassNumber));
        Assert.All(plunges, m => Assert.Equal(3, m.PassCount));
        Assert.Equal(Enumerable.Range(1, 6), plunges.Select(m => m.OperationNumber));
        Assert.All(program.Motions, m => Assert.Equal(6, m.OperationCount));
        Assert.Equal(plan.Toolpaths.Sum(p => p.Points.Length - 1) * 3,
            program.Motions.Count(m => m.Kind == NcMotionKind.Linear && m.Start.Z == m.End.Z));
        Assert.Contains(program.Motions, m => m.Kind == NcMotionKind.Rapid && m.Start.X != m.End.X && m.Start.Z == config.Machining.SafeZ && m.End.Z == config.Machining.SafeZ);
        for (var index = 0; index < program.Motions.Length; index++)
        {
            var motion = program.Motions[index];
            Assert.StartsWith(motion.Kind == NcMotionKind.Rapid ? "G00" : "G01", lines[motion.LineIndex]);
            if (index > 0) { Assert.Equal(program.Motions[index - 1].End, motion.Start); Assert.Equal(program.Motions[index - 1].EndSeconds, motion.StartSeconds); }
        }
        Assert.Equal(config.Machining.SafeZ, program.Motions[^1].End.Z);
    }

    [Fact]
    public void TraditionalModeRetainsContourMajorOperationOrder()
    {
        var config = new ConversionConfig
        {
            Machining = new() { CuttingDepth = -4.5, MaxStepDown = 2, PrioritizePartStability = false },
        };
        var document = PlannerTests.Document(PlannerTests.Rectangle("LEFT", 10, 20, 20, 20), PlannerTests.Rectangle("RIGHT", 100, 20, 20, 20));
        var plan = ToolpathPlanner.Generate(PlannerTests.Request(document, config));
        var program = NcPlaybackParser.Parse(NcGenerator.Generate(plan));
        var plunges = program.Motions.Where(m => m.Kind == NcMotionKind.Linear && m.End.Z < m.Start.Z).ToArray();

        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, plunges.Select(m => m.PathNumber));
        Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, plunges.Select(m => m.PassNumber));
        Assert.Equal(Enumerable.Range(1, 6), plunges.Select(m => m.OperationNumber));
        Assert.All(program.Motions, m => Assert.Equal(6, m.OperationCount));
    }

    [Fact]
    public void ProgramsWithoutPathCommentsRetainZeroOperationMetadata()
    {
        var motion = Assert.Single(NcPlaybackParser.Parse("G0X0Y0Z5\nG1Z-1F60\n").Motions);
        Assert.Equal(0, motion.OperationNumber);
        Assert.Equal(0, motion.OperationCount);
    }

    [Fact]
    public void OperationMetadataFollowsCommentAppearanceRatherThanPathNumbers()
    {
        var code = "G21 G17 G90 G94\nG00 X0 Y0 Z5\n" +
            "(PATH 99 PASS 7/7: LATE)\nG01 Z-1 F60\n" +
            "(PATH 3 PASS 1/2: EARLY)\nG01 X1 F60\n";

        var motions = NcPlaybackParser.Parse(code).Motions;

        Assert.Equal(new[] { 99, 3 }, motions.Select(motion => motion.PathNumber));
        Assert.Equal(new[] { 1, 2 }, motions.Select(motion => motion.OperationNumber));
        Assert.All(motions, motion => Assert.Equal(2, motion.OperationCount));
    }

    [Fact]
    public void LegacyPathMarkerWithoutColonStillProvidesOperationMetadata()
    {
        var code = "G21 G17 G90 G94\nG00 X0 Y0 Z5\n" +
            "(PATH 4 PASS 2/3)\nG01 Z-1 F60\n";

        var motion = Assert.Single(NcPlaybackParser.Parse(code).Motions);

        Assert.Equal(4, motion.PathNumber);
        Assert.Equal(2, motion.PassNumber);
        Assert.Equal(3, motion.PassCount);
        Assert.Equal(string.Empty, motion.PathLabel);
        Assert.Equal(1, motion.OperationNumber);
        Assert.Equal(1, motion.OperationCount);
    }

    [Fact]
    public void ParsingUsesNcRoundingAndInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var program = NcPlaybackParser.Parse("g21g17g90g94\r\ng00z5.000\r\ng00x1.235y2.346\r\nN10g01x2.235f60.5 (comment) ; rest\r\n");
            Assert.Equal(new NcPosition(1.235, 2.346, 5), program.InitialPosition);
            Assert.Equal(60.5, Assert.Single(program.Motions).FeedRate);
            Assert.Equal(new NcPosition(2.235, 2.346, 5), program.Motions[0].End);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("G02 X10 Y10 I5 J0")]
    [InlineData("G03 X10 Y10 R5")]
    [InlineData("G90 G91 G1 X10 F100")]
    [InlineData("G93")]
    [InlineData("G53 G0 X0")]
    [InlineData("G81 Z-5 R1 F100")]
    [InlineData("G41 X10")]
    [InlineData("G18")]
    [InlineData("G95")]
    [InlineData("M98 P1")]
    [InlineData("G1 X10 F0")]
    [InlineData("G1 X10 FNaN")]
    [InlineData("G1 X1.2.3 F100")]
    [InlineData("G1 X1000001 F100")]
    [InlineData("G0 G1 X1 F100")]
    [InlineData("G1 X1 X2 F100")]
    [InlineData("G1 X1 F100 (unclosed")]
    [InlineData("G1 X1 F100 )")]
    public void UnsupportedOrMalformedBlocksFailWithSourceLine(string block)
    {
        var error = Assert.Throws<InvalidDataException>(() => NcPlaybackParser.Parse("G0 X0 Y0 Z5\n" + block));
        Assert.Contains("第 2 行", error.Message);
    }

    [Theory]
    [InlineData("G1 X10 Y10 Z-1 F100")]
    [InlineData("G0 X0 Y0 Z5\nG1 X10")]
    [InlineData("X10 Y10 Z5")]
    [InlineData("G0 X0 Y0 Z5\nM30\nG1 X10 F100")]
    public void MissingModalStateAndInstructionsAfterProgramEndAreRejected(string code)
        => Assert.Throws<InvalidDataException>(() => NcPlaybackParser.Parse(code));

    [Fact]
    public void EmptyAndPositionOnlyProgramsCannotPlay()
    {
        var controller = new NcPlaybackController();
        controller.Load(NcPlaybackParser.Parse("%\n(comment)\n%"));
        Assert.False(controller.CanPlay); Assert.Null(controller.Frame); controller.Play(); Assert.False(controller.IsPlaying);
        controller.Load(NcPlaybackParser.Parse("G0 X5 Y6 Z7"));
        Assert.Equal(new NcPosition(5, 6, 7), controller.Program!.InitialPosition);
        Assert.False(controller.CanPlay); Assert.Null(controller.Frame);
    }

    [Fact]
    public void PlaybackDefaultsToTenTimesSpeed()
    {
        Assert.Equal(10, new NcPlaybackController().Speed);
    }

    [Fact]
    public void ClockUsesElapsedTimeAndSpeedRatherThanTickCount()
    {
        var program = NcPlaybackParser.Parse(SimpleProgram);
        var once = new NcPlaybackController(); once.Load(program); once.SetSpeed(2); once.Play();
        var many = new NcPlaybackController(); many.Load(program); many.SetSpeed(2); many.Play();
        once.Advance(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 20; index++) many.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(4, once.PositionSeconds, 10); Assert.Equal(once.PositionSeconds, many.PositionSeconds, 10);
        Assert.Equal(once.Frame!.Position.Z, many.Frame!.Position.Z, 10);
        Assert.True(once.IsPlaying); Assert.True(many.IsPlaying);
    }

    [Fact]
    public void PauseSeekResetCompletionReplayAndClearAreDeterministic()
    {
        var program = NcPlaybackParser.Parse(SimpleProgram);
        var controller = new NcPlaybackController(); controller.Load(program); controller.SetSpeed(1); controller.Play();
        controller.Advance(TimeSpan.FromSeconds(2)); controller.Pause(); controller.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, controller.PositionSeconds); Assert.False(controller.IsPlaying);
        controller.Seek(.5); Assert.Equal(program.DurationSeconds / 2, controller.PositionSeconds); Assert.False(controller.IsPlaying);
        controller.Play(); controller.Seek(.25); Assert.True(controller.IsPlaying);
        controller.Advance(TimeSpan.FromHours(1)); Assert.False(controller.IsPlaying); Assert.Equal(1, controller.Progress);
        Assert.Equal(program.Motions[^1].End, controller.Frame!.Position);
        controller.Play(); Assert.Equal(0, controller.PositionSeconds); Assert.True(controller.IsPlaying);
        controller.Reset(); Assert.Equal(0, controller.Progress); Assert.False(controller.IsPlaying);
        controller.SetSpeed(5); controller.Play(); controller.Clear();
        Assert.Null(controller.Program); Assert.Null(controller.Frame); Assert.False(controller.CanPlay); Assert.False(controller.IsPlaying);
        Assert.Equal(0, controller.PositionSeconds); Assert.Equal(5, controller.Speed);
        controller.Load(program); Assert.Equal(0, controller.Progress); Assert.False(controller.IsPlaying);
    }

    [Fact]
    public void LoadingAnotherProgramStopsAndRewindsTheClock()
    {
        var controller = new NcPlaybackController(); controller.Load(NcPlaybackParser.Parse(SimpleProgram));
        controller.Play(); controller.Advance(TimeSpan.FromSeconds(2));
        var replacement = NcPlaybackParser.Parse("G0X100Y200Z5\nG1Z-2F100\n");
        controller.Load(replacement);
        Assert.Same(replacement, controller.Program); Assert.False(controller.IsPlaying); Assert.Equal(0, controller.PositionSeconds);
        Assert.Equal(replacement.InitialPosition, controller.Frame!.Position);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(.2)]
    [InlineData(11)]
    public void InvalidSpeedsAreRejected(double speed)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NcPlaybackController().SetSpeed(speed));

    [Fact]
    public void SeekClampsToBoundsAndRejectsNonFiniteInput()
    {
        var controller = new NcPlaybackController(); controller.Load(NcPlaybackParser.Parse(SimpleProgram));
        controller.Seek(2); Assert.Equal(1, controller.Progress);
        controller.Seek(-1); Assert.Equal(0, controller.Progress);
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Seek(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Program!.Sample(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Advance(TimeSpan.FromMilliseconds(-1)));
    }
}
