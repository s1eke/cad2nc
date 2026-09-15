using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cad2Nc.Application;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-session-tests-" + Guid.NewGuid().ToString("N"));
    public SessionTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private CadDocument Document(bool units = false, bool diagnostics = false) => new(Path.Combine(directory, "源 图.dxf"), "source-hash",
        [new Contour("C0001", "板材", new GeometryFactory().CreatePolygon([new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0)]))],
        diagnostics ? [new("reader.warning", "请核对图纸", RequiresConfirmation: true)] : [], units);
    private CadDocument FourBoardDocument() => new(Path.Combine(directory, "四板.dxf"), "four-board-hash",
        [
            new Contour("S1", "母板", new GeometryFactory().CreatePolygon([new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0)]), true),
            new Contour("A", "零件", new GeometryFactory().CreatePolygon([new(10, 10), new(30, 10), new(30, 30), new(10, 30), new(10, 10)])),
            new Contour("S2", "母板", new GeometryFactory().CreatePolygon([new(200, 0), new(300, 0), new(300, 100), new(200, 100), new(200, 0)]), true),
            new Contour("B", "零件", new GeometryFactory().CreatePolygon([new(210, 10), new(230, 10), new(230, 30), new(210, 30), new(210, 10)])),
            new Contour("S3", "母板", new GeometryFactory().CreatePolygon([new(0, 200), new(100, 200), new(100, 300), new(0, 300), new(0, 200)]), true),
            new Contour("C", "零件", new GeometryFactory().CreatePolygon([new(10, 210), new(30, 210), new(30, 230), new(10, 230), new(10, 210)])),
            new Contour("S4", "母板", new GeometryFactory().CreatePolygon([new(200, 200), new(300, 200), new(300, 300), new(200, 300), new(200, 200)]), true),
            new Contour("D", "零件", new GeometryFactory().CreatePolygon([new(210, 210), new(230, 210), new(230, 230), new(210, 230), new(210, 210)]))
        ], [], false);
    private WorkbenchSession Session(CadDocument document, Func<PlanningRequest, CancellationToken, Task<ToolpathPlan>>? planner = null)
    {
        // These session lifecycle regressions exercise the pre-2.6 single-profile fixture.
        var store = new UserStore(Path.Combine(directory, "settings"));
        store.SaveStartupConfig(ConfigurationDefaults.Legacy());
        return new(store, new FakeImporter(document), planner);
    }



    [Fact]
    public void FirstConfigurationAppliedBeforeOpeningADrawingCanBeUndone()
    {
        using var session = Session(Document());
        var defaults = session.Config;
        session.ApplyPreset(defaults with { Tool = defaults.Tool with { ToolNumber = 7, LengthOffset = 9 } });
        Assert.True(session.CanUndo);
        session.Undo();
        Assert.Equal(defaults, session.Config);
        Assert.False(session.CanUndo);
        Assert.False(session.CanExport);
    }

    [Fact]
    public async Task StartupUsesBuiltInDefaultsAndOpeningCadKeepsCurrentSessionParameters()
    {
        var store = new UserStore(Path.Combine(directory, "settings"));
        store.LastConfig = new() { Tool = new() { Diameter = 12 } };
        using var session = new WorkbenchSession(store, new FakeImporter(Document()));
        Assert.Equal(ConfigurationDefaults.Lnc(), session.Config);
        var edited = new ConversionConfig { Tool = new() { ToolNumber = 4, LengthOffset = 7 }, Machining = new() { CuttingFeed = 6000 } };
        session.ApplyPreset(edited);
        await session.LoadCadAsync("first.dxf"); Assert.Equal(edited, session.Config);
        await session.LoadCadAsync("second.dxf"); Assert.Equal(edited, session.Config);
    }

    [Fact]
    public async Task GenerationOutcomeTransitionsAreObservableAndNewDrawingResetsToIdle()
    {
        var complete = new TaskCompletionSource<ToolpathPlan>();
        PlanningRequest? pending = null;
        using var session = Session(Document(), (request, _) => { pending = request; return complete.Task; });
        session.Store.LastView = new()
        {
            HasViewport = true, CenterX = 42, CenterY = 24, Scale = 2,
            LeftPanelOpen = false, ResultHeightRatio = 0.4, ActiveInspector = "Parameters"
        };
        Assert.Equal(GenerationOutcome.Idle, session.GenerationOutcome);
        await session.LoadCadAsync("first.dxf");
        Assert.Equal(GenerationOutcome.Idle, session.GenerationOutcome);
        Assert.False(session.CurrentView.HasViewport);
        Assert.False(session.CurrentView.LeftPanelOpen);
        Assert.Equal(0.4, session.CurrentView.ResultHeightRatio);
        Assert.Equal("Parameters", session.CurrentView.ActiveInspector);

        var transitions = new List<GenerationOutcome>();
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkbenchSession.GenerationOutcome)) transitions.Add(session.GenerationOutcome);
        };
        var generation = session.GenerateAsync();
        Assert.Equal(GenerationOutcome.Running, session.GenerationOutcome);
        complete.SetResult(ToolpathPlanner.Generate(pending!));
        await generation;
        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);

        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 7600 } });
        Assert.Equal(GenerationOutcome.Superseded, session.GenerationOutcome);
        await session.LoadCadAsync("second.dxf");
        Assert.Equal(GenerationOutcome.Idle, session.GenerationOutcome);
        Assert.Equal([
            GenerationOutcome.Running,
            GenerationOutcome.Succeeded,
            GenerationOutcome.Superseded,
            GenerationOutcome.Idle
        ], transitions);
    }

    [Fact]
    public async Task GenerationCanExportWithoutReadingOrAcknowledgingGuidance()
    {
        using var session = Session(Document()); await session.LoadCadAsync("test.dxf");
        await session.GenerateAsync();
        Assert.True(session.CanExport); Assert.True(session.CanCopy); Assert.True(session.CanPlay);
        await session.ExportNcAsync(Path.Combine(directory, "unchecked.nc"));
        Assert.All(session.InspectionItems, item => { Assert.False(item.RequiresConfirmation); Assert.False(item.CanConfirm); });
        Assert.True(session.CanExport); Assert.Equal(0, session.PendingInspectionCount);
        Assert.Contains(session.InspectionItems, item => item.Id == "review.controller");
    }

    [Fact]
    public async Task MultipleMotherboardsCanSelectAndPlayAnIndependentProgram()
    {
        using var session = Session(FourBoardDocument());
        await session.LoadCadAsync("four-board.dxf");

        await session.GenerateAsync();

        Assert.True(session.IsBatchMode);
        Assert.Null(session.Plan);
        Assert.NotNull(session.BatchResult);
        Assert.True(session.BatchResult!.Succeeded);
        Assert.Equal(4, session.BatchResult.Programs.Length);
        Assert.Equal(new[] { "S1", "S2", "S3", "S4" }, session.BatchResult.Programs.Select(p => p.SheetContourId));
        Assert.Equal(new[] { new Point2(0, 0), new Point2(200, 0), new Point2(0, 200), new Point2(200, 200) },
            session.BatchResult.Programs.Select(p => p.Plan.Origin));
        Assert.True(session.CanExport);
        Assert.False(session.CanPlay);
        Assert.False(session.CanCopy);
        Assert.Null(session.SelectedBatchProgram);
        Assert.Empty(session.NcLines);
        Assert.Throws<ArgumentException>(() => session.SelectBatchProgram("missing"));
        Assert.Null(session.SelectedBatchProgram);

        session.SelectBatchProgram("S2");
        Assert.Equal("S2", session.SelectedBatchSheetId);
        var selected = session.SelectedBatchProgram ?? throw new Xunit.Sdk.XunitException("Expected selected batch program");
        Assert.Same(session.BatchResult.Programs[1], selected);
        Assert.True(session.CanPlay);
        Assert.True(session.CanCopy);
        Assert.Equal(NcGenerator.FormatForMachine(selected.Code, session.Config, windowsLineEndings: true), session.NcCode);
        Assert.Equal(selected.Code.Split('\n').Select(line => line.TrimEnd('\r')), session.NcLines);
        Assert.Same(selected.Playback, session.Playback.Program);

        session.Playback.Play();
        Assert.True(session.Playback.IsPlaying);
        session.SelectBatchProgram("S3");
        Assert.False(session.Playback.IsPlaying);
        Assert.Equal(0, session.Playback.PositionSeconds);
        Assert.Equal("S3", session.SelectedBatchSheetId);

        session.SelectBatchProgram(null);
        Assert.Null(session.SelectedBatchProgram);
        Assert.False(session.CanPlay);
        Assert.False(session.CanCopy);
        Assert.Empty(session.NcLines);

        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 7_600 } });
        Assert.Null(session.BatchResult);
        Assert.False(session.CanExport);
    }

    [Fact]
    public void MachineGuidanceIsAvailableBeforeOpeningAnyFile()
    {
        using var session = Session(Document());
        var guidance = session.InspectionItems.Where(item => item.Kind == InspectionKind.Guidance).ToArray();
        Assert.Contains(guidance, item => item.Id == "review.tool");
        Assert.Contains(guidance, item => item.Id == "review.depth");
        Assert.Contains(guidance, item => item.Id == "review.origin");
        Assert.Contains(guidance, item => item.Id == "review.controller");
        Assert.All(guidance, item =>
        {
            Assert.False(item.RequiresConfirmation); Assert.False(item.CanConfirm);
            Assert.NotEmpty(item.Details); Assert.Empty(item.ActionLabel);
        });
    }

    [Fact]
    public async Task SameRevisionRegenerationExportsImmediatelyAndOldCardsCannotChangeEligibility()
    {
        using var session = Session(Document()); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        var oldCards = session.InspectionItems.ToArray();
        var revision = session.Revision;
        await session.GenerateAsync();
        Assert.Equal(revision, session.Revision); Assert.Equal(0, session.PendingInspectionCount); Assert.True(session.CanExport);
        foreach (var card in oldCards) session.SetInspectionConfirmed(card, true);
        Assert.Equal(0, session.PendingInspectionCount); Assert.True(session.CanExport);
        var oldDrawing = session.InspectionItems.Single(i => i.Id == "review.drawing");
        await session.LoadCadAsync("test.dxf"); session.SetInspectionConfirmed(oldDrawing, true);
        Assert.False(session.InspectionItems.Single(i => i.Id == "review.drawing").IsConfirmed);
    }

    [Fact]
    public async Task EditingRequiresRegenerationButNeverRequiresManualReview()
    {
        using var session = Session(Document(true, true)); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        session.UnitsConfirmed = true; session.DiagnosticsConfirmed = true; Assert.True(session.CanExport);
        session.UpdateConfig(session.Config with { Tool = session.Config.Tool with { LengthOffset = 7 } });
        Assert.False(session.CanExport); Assert.Equal(0, session.PendingInspectionCount);
        session.Undo(); await session.GenerateAsync(); Assert.True(session.CanExport); Assert.Equal(0, session.PendingInspectionCount);
    }

    [Fact]
    public async Task PlanningWarningsRemainVisibleWithoutPreventingExport()
    {
        var document = Document();
        using var session = Session(document, (request, _) => Task.FromResult(ToolpathPlanner.Generate(request) with
        { Diagnostics = [new("nested_outside", "请确认加工侧", ContourId: "C0001", RequiresConfirmation: true, RelatedField: "CutSide")] }));
        await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        Assert.True(session.CanExport); Assert.Equal(0, session.PendingInspectionCount);
        var item = session.InspectionItems.Single(i => i.Id.StartsWith("diagnostic.nested_outside", StringComparison.Ordinal));
        Assert.Equal(InspectionScope.Generation, item.Scope); Assert.Equal("CutSide", item.RelatedField); Assert.Contains("C0001", item.ContourIds);
        session.SetInspectionConfirmed(item, true); Assert.True(session.CanExport);
        await session.GenerateAsync(); Assert.True(session.CanExport);
    }

    [Fact]
    public async Task DiagnosticErrorsCannotBeAcknowledgedToAuthorizeExport()
    {
        using var session = Session(Document(), (request, _) => Task.FromResult(ToolpathPlanner.Generate(request) with
        { Diagnostics = [new("plan.error", "未通过", DiagnosticSeverity.Error, RequiresConfirmation: true)] }));
        await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        var item = session.InspectionItems.Single(i => i.Id.StartsWith("diagnostic.plan.error", StringComparison.Ordinal));
        Assert.Equal(InspectionStatus.Blocked, item.Status); Assert.False(item.RequiresConfirmation);
        session.SetInspectionConfirmed(item, true); Assert.False(session.CanExport);
    }

    [Fact]
    public async Task PlanningFailureKeepsStructuredRepairTargets()
    {
        var failure = new Diagnostic("plan.tool_fit", "刀具无法通过", DiagnosticSeverity.Error, "C0001", RelatedField: "Diameter");
        using var session = Session(Document(), (_, _) => Task.FromException<ToolpathPlan>(new ToolpathGenerationException("不能生成", [failure])));
        await session.LoadCadAsync("test.dxf"); await Assert.ThrowsAsync<ToolpathGenerationException>(() => session.GenerateAsync());
        Assert.Equal(GenerationOutcome.Failed, session.GenerationOutcome);
        Assert.Contains(failure, session.Diagnostics); Assert.DoesNotContain(session.Diagnostics, d => d.Code == "plan.failed");
        var item = session.InspectionItems.Single(i => i.Id.StartsWith("diagnostic.plan.tool_fit", StringComparison.Ordinal));
        Assert.Equal("Diameter", item.RelatedField); Assert.Contains("C0001", item.ContourIds); Assert.Equal(InspectionStatus.Blocked, item.Status);
    }

    [Fact]
    public async Task ExplicitGenerationAndEveryEditInvalidateExport()
    {
        using var session = Session(Document());
        await session.LoadCadAsync("test.dxf"); Assert.True(session.CanGenerate); Assert.False(session.CanExport); AssertPlaybackCleared(session);
        await session.GenerateAsync(); Assert.True(session.CanExport); Assert.True(session.CanPlay);
        session.Playback.Play(); session.Playback.Advance(TimeSpan.FromMilliseconds(250));
        var oldPlan = session.Plan;
        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 9000 } });
        Assert.False(session.CanExport); Assert.True(session.HasStalePlan); Assert.Same(oldPlan, session.Plan); Assert.Empty(session.NcLines);
        AssertPlaybackCleared(session);
        Assert.DoesNotContain(session.Diagnostics, d => d.Code == "plan.complete");
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportNcAsync(Path.Combine(directory, "stale.nc")));
        session.Undo(); Assert.Equal(10000, session.Config.Machining.CuttingFeed); Assert.False(session.CanExport); AssertPlaybackCleared(session);
        await session.GenerateAsync(); Assert.True(session.CanExport); Assert.True(session.CanPlay); session.Playback.Play();
        session.Redo(); Assert.Equal(9000, session.Config.Machining.CuttingFeed); AssertPlaybackCleared(session);
        await session.GenerateAsync(); Assert.True(session.CanPlay); session.Playback.Play();
        session.Contours[0].Side = CutSide.Inside; Assert.False(session.CanExport); AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task DisplaySelectionAndViewportDoNotInvalidateOutput()
    {
        using var session = Session(Document()); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        var revision = session.Revision;
        var playback = session.Playback.Program;
        session.Playback.Play(); session.Playback.Advance(TimeSpan.FromMilliseconds(250));
        var position = session.Playback.PositionSeconds; var frame = session.Playback.Frame;
        session.Contours[0].IsFocused = true;
        session.CurrentView = session.CurrentView with { Scale = 2, ShowFinished = false };
        Assert.Equal(revision, session.Revision); Assert.True(session.CanExport);
        Assert.True(session.CanPlay); Assert.Same(playback, session.Playback.Program); Assert.True(session.Playback.IsPlaying);
        Assert.Equal(position, session.Playback.PositionSeconds); Assert.Same(frame, session.Playback.Frame);
    }

    [Fact]
    public async Task LateGenerationCannotOverwriteEditedState()
    {
        var started = new TaskCompletionSource<PlanningRequest>();
        var complete = new TaskCompletionSource<ToolpathPlan>();
        using var session = Session(Document(), (request, _) => { started.SetResult(request); return complete.Task; });
        await session.LoadCadAsync("test.dxf"); var generation = session.GenerateAsync(); var request = await started.Task;
        Assert.Equal(GenerationOutcome.Running, session.GenerationOutcome);
        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 8000 } });
        Assert.Equal(GenerationOutcome.Superseded, session.GenerationOutcome);
        complete.SetResult(ToolpathPlanner.Generate(request)); await generation;
        Assert.Equal(GenerationOutcome.Superseded, session.GenerationOutcome);
        Assert.Null(session.Plan); Assert.False(session.CanExport); Assert.Empty(session.NcLines);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task CancellationDiscardsEvenNonCooperativeResults()
    {
        var complete = new TaskCompletionSource<ToolpathPlan>(); PlanningRequest? request = null;
        using var session = Session(Document(), (r, _) => { request = r; return complete.Task; });
        await session.LoadCadAsync("test.dxf"); var generation = session.GenerateAsync();
        Assert.Equal(GenerationOutcome.Running, session.GenerationOutcome);
        session.Cancel(); Assert.Equal(GenerationOutcome.Running, session.GenerationOutcome);
        AssertPlaybackCleared(session);
        complete.SetResult(ToolpathPlanner.Generate(request!)); await generation;
        Assert.Equal(GenerationOutcome.Canceled, session.GenerationOutcome);
        Assert.False(session.CanExport); Assert.Null(session.Plan); Assert.False(session.IsBusy);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task FailedRegenerationCannotExportOldNc()
    {
        int calls = 0;
        using var session = Session(Document(), (r, _) => ++calls == 1 ? Task.FromResult(ToolpathPlanner.Generate(r)) : Task.FromException<ToolpathPlan>(new ToolpathGenerationException("failed")));
        await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        Assert.True(session.CanPlay); session.Playback.Play(); session.Playback.Advance(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<ToolpathGenerationException>(() => session.GenerateAsync());
        Assert.Equal(GenerationOutcome.Failed, session.GenerationOutcome);
        Assert.False(session.CanExport); Assert.True(session.HasStalePlan); Assert.Empty(session.NcLines);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task UnitAndImportWarningsNeverRequireAcknowledgement()
    {
        using var session = Session(Document(true, true)); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        Assert.True(session.CanExport); Assert.True(session.CanPlay);
        Assert.Contains(session.InspectionItems, i => i.Id == "drawing.units" && i.Status == InspectionStatus.Information);
        Assert.Contains(session.Diagnostics, d => d.Code == "reader.warning");
        session.Playback.Play();
        await session.LoadCadAsync("test.dxf"); Assert.Null(session.Plan); Assert.False(session.CanExport);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task BatchChangesAndPresetEachHaveOneUndoStep()
    {
        using var session = Session(Document()); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync(); session.Playback.Play();
        session.SetIncluded(["C0001"], false); Assert.False(session.Contours[0].IsIncluded); AssertPlaybackCleared(session);
        session.Undo(); Assert.True(session.Contours[0].IsIncluded); Assert.False(session.CanUndo); AssertPlaybackCleared(session);
        await session.GenerateAsync(); session.Playback.Play();
        session.ApplyPreset(new() { Tool = new() { Diameter = 4 }, Machining = new() { CuttingFeed = 6000 } });
        AssertPlaybackCleared(session);
        session.Undo(); Assert.Equal(new ConversionConfig(), session.Config); AssertPlaybackCleared(session);
        await session.GenerateAsync(); session.Playback.Play();
        session.Redo(); Assert.Equal(4, session.Config.Tool.Diameter); AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task InvalidTextCannotReenablePriorOutput()
    {
        using var session = Session(Document()); await session.LoadCadAsync("test.dxf"); await session.GenerateAsync();
        session.Playback.Play();
        session.SetInputError("Diameter", "数字无效"); Assert.False(session.CanExport); Assert.False(session.CanGenerate); AssertPlaybackCleared(session);
        session.SetInputError("Diameter", null); Assert.False(session.CanExport); Assert.True(session.CanGenerate); AssertPlaybackCleared(session);
        session.Undo(); Assert.False(session.CanGenerate); AssertPlaybackCleared(session);
        session.Undo(); Assert.True(session.CanGenerate); Assert.False(session.CanExport); AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task DistinctRejectedNumbersWithTheSameErrorUndoAndRedoExactly()
    {
        using var session = Session(Document()); await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        var revision = session.Revision;
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 2.5);
        Assert.Equal(revision + 1, session.Revision);
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var first)); Assert.Equal(2.5, first);
        Assert.Equal(2, session.Config.Tool.LengthOffset); Assert.False(session.CanGenerate); Assert.False(session.CanExport);
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 2.5);
        Assert.Equal(revision + 1, session.Revision);
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 3.5);
        Assert.Equal(revision + 2, session.Revision);
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var second)); Assert.Equal(3.5, second);
        session.Undo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var undone)); Assert.Equal(2.5, undone);
        Assert.Equal("请输入整数", session.ValidationErrors["LengthOffset"]); Assert.False(session.CanGenerate);
        session.Undo();
        Assert.False(session.TryGetRejectedNumber("LengthOffset", out _)); Assert.Empty(session.ValidationErrors);
        Assert.False(session.CanUndo); Assert.True(session.CanGenerate); Assert.False(session.CanExport);
        session.Redo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var redoneFirst)); Assert.Equal(2.5, redoneFirst);
        session.Redo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var redoneSecond)); Assert.Equal(3.5, redoneSecond);
        Assert.False(session.CanRedo); Assert.False(session.CanGenerate); AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task ClearingResettingAndImportingHandleRejectedNumbersWithTheirErrors()
    {
        using var session = Session(Document()); await session.LoadCadAsync("fixture.dxf");
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 2.5);
        session.SetInputError("LengthOffset", null);
        Assert.False(session.TryGetRejectedNumber("LengthOffset", out _)); Assert.True(session.CanGenerate);
        session.Undo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var restored)); Assert.Equal(2.5, restored);
        Assert.False(session.CanGenerate);
        session.ApplyPreset(new() { Tool = new() { ToolNumber = 5, LengthOffset = 7 } });
        Assert.False(session.TryGetRejectedNumber("LengthOffset", out _)); Assert.True(session.CanGenerate);
        session.Undo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var priorPreset)); Assert.Equal(2.5, priorPreset);
        Assert.Equal(2, session.Config.Tool.LengthOffset); Assert.False(session.CanGenerate);
        session.Redo(); Assert.Equal(7, session.Config.Tool.LengthOffset); Assert.False(session.TryGetRejectedNumber("LengthOffset", out _));
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 7.5);
        await session.LoadCadAsync("next.dxf");
        Assert.False(session.TryGetRejectedNumber("LengthOffset", out _)); Assert.Empty(session.ValidationErrors);
        Assert.Equal(7, session.Config.Tool.LengthOffset); Assert.False(session.CanUndo);
        session.SetInputError("LengthOffset", "请输入整数", rejectedValue: 7.5);
        session.RestoreDefaults();
        Assert.Equal(new ConversionConfig(), session.Config); Assert.False(session.TryGetRejectedNumber("LengthOffset", out _));
        session.Undo();
        Assert.True(session.TryGetRejectedNumber("LengthOffset", out var resetUndo)); Assert.Equal(7.5, resetUndo);
        Assert.Equal(7, session.Config.Tool.LengthOffset); Assert.False(session.CanGenerate);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonfiniteRejectedNumbersRemainUndoableAndNeverEnterSavedTasks(double rejected)
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        using var session = Session(document); await session.LoadCadAsync(document.SourcePath);
        var taskPath = Path.Combine(directory, "rejected-input.cad2nc.json");
        await session.SaveTaskAsync(taskPath);
        var originalTask = File.ReadAllText(taskPath);
        session.SetInputError("Diameter", "请输入有效数字", rejectedValue: rejected);
        Assert.True(session.IsDirty); Assert.False(session.CanGenerate);
        Assert.True(session.TryGetRejectedNumber("Diameter", out var value)); Assert.True(rejected.Equals(value));
        var revision = session.Revision;
        session.SetInputError("Diameter", "请输入有效数字", rejectedValue: rejected); Assert.Equal(revision, session.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveTaskAsync(taskPath));
        Assert.Equal(originalTask, File.ReadAllText(taskPath));
        session.Undo(); Assert.False(session.TryGetRejectedNumber("Diameter", out _)); Assert.False(session.IsDirty);
        session.Redo(); Assert.True(session.TryGetRejectedNumber("Diameter", out var redone)); Assert.True(rejected.Equals(redone));
        session.RestoreDefaults(); await session.SaveTaskAsync(taskPath);
        Assert.Equal(originalTask, File.ReadAllText(taskPath));
        session.SetInputError("Diameter", "请输入有效数字", rejectedValue: rejected);
        await session.LoadTaskAsync(taskPath);
        Assert.False(session.TryGetRejectedNumber("Diameter", out _)); Assert.Empty(session.ValidationErrors);
    }

    [Fact]
    public async Task RestoringSameDefaultsClearsInvalidInputAsOneUndoableEdit()
    {
        using var session = Session(Document()); await session.LoadCadAsync("fixture.dxf");
        session.SetInputError("Diameter", "无效数字"); var revision = session.Revision;
        session.RestoreDefaults(); Assert.True(session.CanGenerate); Assert.True(session.Revision > revision);
        session.Undo(); Assert.False(session.CanGenerate);
        session.Redo(); Assert.True(session.CanGenerate);
    }

    [Fact]
    public async Task SavingTaskDoesNotSaveExportAuthorization()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        var taskPath = Path.Combine(directory, "加工 任务.cad2nc.json");
        using var first = Session(document); await first.LoadCadAsync(document.SourcePath); await first.GenerateAsync(); await first.SaveTaskAsync(taskPath);
        Assert.False(first.IsDirty); Assert.True(first.CanPlay);
        first.Playback.Play(); first.Playback.Advance(TimeSpan.FromMilliseconds(250));
        using var reopened = Session(document); await reopened.LoadTaskAsync(taskPath);
        Assert.False(reopened.CanExport); Assert.Null(reopened.Plan); Assert.Equal(first.Config, reopened.Config); Assert.False(reopened.IsDirty);
        AssertPlaybackCleared(reopened);
    }

    [Fact]
    public async Task DeduplicatedContoursResetOldTaskSelectionEvenWhenSourceHashIsUnchanged()
    {
        var document = Document();
        File.WriteAllText(document.SourcePath, "fixture");
        var duplicate = new Contour("C0002", document.Contours[0].Layer, document.Contours[0].Geometry);
        var oldDocument = document with { Contours = document.Contours.Add(duplicate) };
        var taskPath = Path.Combine(directory, "before-deduplication.cad2nc.json");
        var config = ConfigurationDefaults.Legacy() with { Tool = new() { Diameter = 8 } };
        using (var old = Session(oldDocument))
        {
            await old.LoadCadAsync(document.SourcePath);
            old.UpdateConfig(config);
            old.SetIncluded(["C0001", "C0002"], false);
            await old.SaveTaskAsync(taskPath);
        }
        using var reopened = Session(document);
        await reopened.LoadTaskAsync(taskPath);
        Assert.Equal(config, reopened.Config);
        Assert.True(Assert.Single(reopened.Contours).IsIncluded);
        Assert.Equal(CutSide.Outside, reopened.Contours[0].Side);
        Assert.True(reopened.IsDirty);
        Assert.False(reopened.CanExport);
        Assert.Contains(reopened.Diagnostics, d => d.Code == "task.source_changed");
        await reopened.GenerateAsync();
        Assert.Single(reopened.Plan!.Toolpaths);
        Assert.True(reopened.CanExport);
    }

    [Fact]
    public async Task ChangedSourceResetsSelectionRatherThanReusingIds()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture"); var taskPath = Path.Combine(directory, "task.cad2nc.json");
        using (var session = Session(document)) { await session.LoadCadAsync(document.SourcePath); session.SetIncluded(["C0001"], false); await session.SaveTaskAsync(taskPath); }
        using var changed = Session(document with { SourceHash = "changed" }); await changed.LoadTaskAsync(taskPath);
        Assert.True(changed.Contours[0].IsIncluded); Assert.True(changed.IsDirty); Assert.Contains(changed.Diagnostics, d => d.Code == "task.source_changed");
        changed.UpdateConfig(changed.Config with { Machining = changed.Config.Machining with { CuttingFeed = 7000 } });
        Assert.Contains(changed.Diagnostics, d => d.Code == "task.source_changed");
        await changed.GenerateAsync(); Assert.True(changed.CanExport); Assert.Contains(changed.Diagnostics, d => d.Code == "task.source_changed");
    }

    [Fact]
    public async Task MissingSourceCanBeRelocatedWithoutSavingOldResults()
    {
        var document = Document(); var taskPath = Path.Combine(directory, "task.cad2nc.json");
        TaskStorage.Save(taskPath, document, new(), document.DefaultSelection, new());
        using var session = Session(document); await Assert.ThrowsAsync<SourceFileMissingException>(() => session.LoadTaskAsync(taskPath));
        var moved = Path.Combine(directory, "移动后的图纸.dxf"); File.WriteAllText(moved, "fixture");
        await session.LoadTaskAsync(taskPath, moved); Assert.NotNull(session.Document); Assert.False(session.CanExport);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public void CorruptTaskAndFutureVersionAreRejected()
    {
        var path = Path.Combine(directory, "bad.cad2nc.json"); File.WriteAllText(path, "{bad json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => TaskStorage.Read(path));
        File.WriteAllText(path, "{\"schemaVersion\":99}"); Assert.Throws<InvalidDataException>(() => TaskStorage.Read(path));
    }

    [Fact]
    public void PreviousTaskSchemaIsRejected()
    {
        var document = Document();
        var taskPath = Path.Combine(directory, "old-view.cad2nc.json");
        TaskStorage.Save(taskPath, document, new(), document.DefaultSelection,
            new() { LeftWidth = 280, RightWidth = 360, ResultHeightRatio = 0.5, ActiveInspector = "Parameters" });
        var oldTask = JsonNode.Parse(File.ReadAllText(taskPath))!.AsObject();
        oldTask["schemaVersion"] = 1;
        var oldView = oldTask["view"]!.AsObject();
        oldView.Remove("resultHeightRatio");
        oldView.Remove("activeInspector");
        File.WriteAllText(taskPath, oldTask.ToJsonString());

        Assert.Throws<InvalidDataException>(() => TaskStorage.Read(taskPath));
    }

    [Fact]
    public void PreviousTaskWithMalformedCurrentFieldsIsRejected()
    {
        var document = Document();
        var taskPath = Path.Combine(directory, "old-config.cad2nc.json");
        TaskStorage.Save(taskPath, document, new(), document.DefaultSelection, new());
        var oldTask = JsonNode.Parse(File.ReadAllText(taskPath))!.AsObject();
        oldTask["schemaVersion"] = 1;
        var config = oldTask["config"]!.AsObject();
        config["originMode"] = "not-a-current-origin";
        config["machine"] = "not-a-machine-object";
        var machining = config["machining"]!.AsObject();
        machining["boardThickness"] = "not-a-number";
        machining["zReference"] = "not-a-z-reference";
        machining["layeredCutting"] = "not-a-boolean";
        machining["prioritizePartStability"] = "not-a-boolean";
        machining["entryMode"] = 42;
        File.WriteAllText(taskPath, oldTask.ToJsonString());
        var original = File.ReadAllText(taskPath);

        Assert.Throws<InvalidDataException>(() => TaskStorage.Read(taskPath));
        Assert.Equal(original, File.ReadAllText(taskPath));
    }

    [Fact]
    public async Task OpeningPreviousTaskSchemaIsRejected()
    {
        var document = Document();
        File.WriteAllText(document.SourcePath, "fixture");
        var taskPath = Path.Combine(directory, "legacy-v2.cad2nc.json");
        TaskStorage.Save(taskPath, document, new(), document.DefaultSelection, new());
        var task = JsonNode.Parse(File.ReadAllText(taskPath))!.AsObject();
        task["schemaVersion"] = 2;
        task["config"]!["machining"]!.AsObject().Remove("prioritizePartStability");
        File.WriteAllText(taskPath, task.ToJsonString());
        using var session = Session(document);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.LoadTaskAsync(taskPath));
    }

    [Fact]
    public async Task NcReviewWidthStaysLocalWithoutChangingTaskDataOrOutput()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        using var session = Session(document);
        await session.LoadCadAsync(document.SourcePath);
        await session.GenerateAsync();
        session.CurrentView = session.CurrentView with { LeftWidth = 280, RightWidth = 360 };
        var taskPath = Path.Combine(directory, "local-layout.cad2nc.json");
        await session.SaveTaskAsync(taskPath);
        var taskJson = File.ReadAllText(taskPath);
        var view = session.CurrentView; var revision = session.Revision; var plan = session.Plan; var nc = session.NcLines;

        session.Store.NcReviewWidth = 672;
        session.Store.Save();
        Assert.False(session.IsDirty);
        Assert.Equal(revision, session.Revision);
        Assert.Same(view, session.CurrentView);
        Assert.Same(plan, session.Plan);
        Assert.Same(nc, session.NcLines);
        Assert.True(session.CanExport);
        Assert.True(session.CanPlay);
        await session.SaveTaskAsync(taskPath);
        Assert.Equal(taskJson, File.ReadAllText(taskPath));
        var task = JsonNode.Parse(taskJson)!.AsObject();
        Assert.False(task.ContainsKey("ncReviewWidth"));
        Assert.False(task["view"]!.AsObject().ContainsKey("ncReviewWidth"));
         Assert.Equal(3, TaskStorage.Read(taskPath).SchemaVersion);

        using var reopened = Session(document);
        await reopened.LoadTaskAsync(taskPath);
        Assert.Equal(672, reopened.Store.NcReviewWidth);
        Assert.Equal(280, reopened.CurrentView.LeftWidth);
        Assert.Equal(360, reopened.CurrentView.RightWidth);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task CorruptNewTaskPreservesPreviousDocumentAndPausesPlayback()
    {
        using var session = Session(Document()); await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        session.Playback.Play();
        var path = Path.Combine(directory, "broken.cad2nc.json"); File.WriteAllText(path, "{bad json");
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => session.LoadTaskAsync(path));
        Assert.True(session.CanExport); Assert.False(session.HasStalePlan);
        Assert.True(session.CanPlay); Assert.False(session.Playback.IsPlaying);
    }

    [Fact]
    public async Task PlaybackAndExportBothSupportWarningOnlyDocuments()
    {
        using var session = Session(Document(units: true, diagnostics: true));
        await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        Assert.True(session.CanExport); Assert.True(session.CanPlay); Assert.Null(session.PlaybackError);
        var program = session.Playback.Program;
        session.Playback.Play(); session.Playback.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(session.Playback.IsPlaying); Assert.True(session.Playback.PositionSeconds > 0);
        session.UnitsConfirmed = true;
        Assert.True(session.CanPlay); Assert.True(session.CanExport); Assert.Same(program, session.Playback.Program);
        session.DiagnosticsConfirmed = true;
        Assert.True(session.CanExport); Assert.True(session.CanPlay); Assert.True(session.Playback.IsPlaying);
        Assert.Same(program, session.Playback.Program);
    }

    [Fact]
    public async Task PlaybackControlsDoNotDirtySavedTaskOrChangeItsOutput()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        using var session = Session(document); await session.LoadCadAsync(document.SourcePath); await session.GenerateAsync();
        await session.SaveTaskAsync(Path.Combine(directory, "playback.cad2nc.json"));
        var revision = session.Revision; var view = session.CurrentView; var config = session.Config; var nc = session.NcLines;
        Assert.False(session.IsDirty);
        foreach (var action in new Action[]
        {
            session.Playback.Play,
            () => session.Playback.Advance(TimeSpan.FromMilliseconds(250)),
            () => session.Playback.SetSpeed(5),
            session.Playback.Pause,
            () => session.Playback.Seek(.5),
            session.Playback.Play,
            () => session.Playback.Advance(TimeSpan.FromHours(1)),
            session.Playback.Reset
        })
        {
            action();
            Assert.False(session.IsDirty); Assert.Equal(revision, session.Revision);
            Assert.Same(config, session.Config); Assert.Same(view, session.CurrentView); Assert.Same(nc, session.NcLines);
            Assert.True(session.CanPlay); Assert.True(session.CanExport);
        }
    }

    [Fact]
    public async Task ThemeAndSaveDirectoriesDoNotDirtySavedTaskOrInvalidateOutput()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        using var session = Session(document); await session.LoadCadAsync(document.SourcePath); await session.GenerateAsync();
        var taskPath = Path.Combine(directory, "preferences.cad2nc.json");
        await session.SaveTaskAsync(taskPath);
        var taskContents = File.ReadAllText(taskPath);
        var revision = session.Revision; var plan = session.Plan; var nc = session.NcLines;
        var config = session.Config; var playback = session.Playback.Program;
        var canUndo = session.CanUndo; var canRedo = session.CanRedo;

        void AssertTaskUnchanged()
        {
            Assert.False(session.IsDirty); Assert.Equal(revision, session.Revision);
            Assert.False(session.HasStalePlan); Assert.True(session.CanExport); Assert.True(session.CanPlay);
            Assert.Same(plan, session.Plan); Assert.Same(nc, session.NcLines); Assert.Same(config, session.Config);
            Assert.Same(playback, session.Playback.Program);
            Assert.Equal(canUndo, session.CanUndo); Assert.Equal(canRedo, session.CanRedo);
            Assert.Equal(taskContents, File.ReadAllText(taskPath));
        }

        foreach (var theme in new[] { "Light", "Dark", "Default" })
        {
            session.Store.Theme = theme;
            session.CurrentView = session.CurrentView with { Theme = theme };
            session.Store.Save();
            Assert.Equal(theme, session.CurrentView.Theme);
            AssertTaskUnchanged();
        }
        session.Store.UpdateTaskDirectory(Path.Combine(directory, "任务目录")); AssertTaskUnchanged();
        session.Store.UpdateNcDirectory(Path.Combine(directory, "NC目录")); AssertTaskUnchanged();
        session.Store.UpdateTaskDirectory(null); AssertTaskUnchanged();
        session.Store.UpdateNcDirectory(null); AssertTaskUnchanged();
    }

    [Theory]
    [InlineData("Light", "Dark")]
    [InlineData("Dark", "Light")]
    [InlineData("Dark", "Default")]
    public async Task OpeningTaskUsesLocalLayoutAndGlobalThemeWhileKeepingSavedTaskClean(string savedTheme, string globalTheme)
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        var taskPath = Path.Combine(directory, "old-theme.cad2nc.json");
        var savedView = new ViewState
        {
            Theme = savedTheme, HasViewport = true, CenterX = 12, CenterY = 34, Scale = 2,
            ShowFinished = false, LeftPanelOpen = false, RightWidth = 420
        };
        var savedConfig = new ConversionConfig
        {
            Tool = new() { ToolNumber = 8, LengthOffset = 11, Diameter = 4 },
            Machining = new() { CuttingFeed = 6400 }
        };
        var savedSelection = document.DefaultSelection.SetItem("C0001", new(false, CutSide.Inside));
        TaskStorage.Save(taskPath, document, savedConfig, savedSelection, savedView);
        using var session = Session(document);
        var localView = new ViewState
        {
            Theme = "Default", HasViewport = true, CenterX = 90, CenterY = 80, Scale = 3,
            ShowSource = false, RightPanelOpen = false, LeftWidth = 390,
            ResultHeightRatio = 0.44, ActiveInspector = "Parameters"
        };
        session.Store.LastView = localView;
        session.Store.Theme = globalTheme;
        await session.LoadTaskAsync(taskPath);
        var expectedLocalView = localView with { Theme = globalTheme, HasViewport = false };
        Assert.Equal(expectedLocalView, session.CurrentView);
        Assert.Equal(expectedLocalView, session.Store.LastView);
        Assert.Equal(savedConfig, session.Config);
        Assert.False(session.Contours[0].IsIncluded);
        Assert.Equal(CutSide.Inside, session.Contours[0].Side);
        Assert.Equal(globalTheme, session.Store.Theme);
        Assert.False(session.IsDirty); Assert.False(session.CanUndo); Assert.False(session.CanRedo);
        Assert.False(session.CanExport); Assert.Null(session.Plan);
        Assert.Equal(savedTheme, TaskStorage.Read(taskPath).View.Theme);
    }

    [Fact]
    public async Task PureViewChangesNeverDirtySavedTaskOrInvalidateOutput()
    {
        var document = Document(); File.WriteAllText(document.SourcePath, "fixture");
        using var session = Session(document); await session.LoadCadAsync(document.SourcePath); await session.GenerateAsync();
        await session.SaveTaskAsync(Path.Combine(directory, "view.cad2nc.json"));
        var savedView = session.CurrentView; var revision = session.Revision; var plan = session.Plan; var nc = session.NcLines;
        var dirtyNotifications = 0;
        session.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(WorkbenchSession.IsDirty)) dirtyNotifications++; };
        session.Contours[0].ToolpathOrderText = "顺序 1";
        Assert.False(session.IsDirty); Assert.Equal(revision, session.Revision); Assert.True(session.CanExport);
        foreach (var changedView in new[]
        {
            savedView with { CenterX = 20, HasViewport = true },
            savedView with { ShowToolpaths = false },
            savedView with { LeftPanelOpen = false },
            savedView with { RightWidth = 420 },
            savedView with { ResultHeightRatio = 0.5 },
            savedView with { ActiveInspector = "Parameters" }
        })
        {
            session.CurrentView = changedView with { Theme = "Dark" };
            Assert.False(session.IsDirty); Assert.Equal(revision, session.Revision);
            Assert.True(session.CanExport); Assert.True(session.CanPlay); Assert.Same(plan, session.Plan); Assert.Same(nc, session.NcLines);
        }
        Assert.Equal(0, dirtyNotifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegenerationClearsPlayingOutputBeforeAwaitingAndCannotRestoreDiscardedResults(bool cancel)
    {
        var complete = new TaskCompletionSource<ToolpathPlan>();
        PlanningRequest? pending = null; var calls = 0;
        using var session = Session(Document(), (request, _) =>
        {
            if (++calls == 1) return Task.FromResult(ToolpathPlanner.Generate(request));
            pending = request;
            return complete.Task;
        });
        await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        var oldPlan = session.Plan;
        session.Playback.Play(); session.Playback.Advance(TimeSpan.FromMilliseconds(250)); Assert.True(session.Playback.IsPlaying);
        var generation = session.GenerateAsync();
        Assert.True(session.IsBusy); AssertPlaybackCleared(session);
        if (cancel) session.Cancel();
        else session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 7000 } });
        complete.SetResult(ToolpathPlanner.Generate(pending!)); await generation;
        Assert.Same(oldPlan, session.Plan); Assert.True(session.HasStalePlan); Assert.False(session.IsBusy); Assert.Empty(session.NcLines);
        AssertPlaybackCleared(session);
    }

    [Fact]
    public async Task FailedCadImportPreservesPreviousDocumentAndPausesPlayback()
    {
        var importer = new FailingImporter(Document());
        var store = new UserStore(Path.Combine(directory, "settings"));
        store.SaveStartupConfig(ConfigurationDefaults.Legacy());
        using var session = new WorkbenchSession(store, importer);
        await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);
        session.Playback.Play(); Assert.True(session.Playback.IsPlaying);
        importer.Fail = true;
        await Assert.ThrowsAsync<CadImportException>(() => session.LoadCadAsync("broken.dxf"));
        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);
        Assert.NotNull(session.Document); Assert.NotNull(session.Plan); Assert.NotEmpty(session.NcLines); Assert.True(session.CanExport);
        Assert.False(session.Playback.IsPlaying); Assert.True(session.CanPlay);
    }

    [Fact]
    public void LegacySettingsFileIsIgnored()
    {
        var legacy = Path.Combine(directory, "legacy.json"); const string json = "{\"tool_number\":\"3\",\"tool_diameter\":\"-2\",\"cut_feed\":\"8000\"}";
        File.WriteAllText(legacy, json); var store = new UserStore(Path.Combine(directory, "settings"));
        Assert.Equal(new ConversionConfig(), store.LastConfig);
        store.Save(); Assert.Equal(json, File.ReadAllText(legacy));
    }

    private static void AssertPlaybackCleared(WorkbenchSession session)
    {
        Assert.False(session.CanPlay); Assert.False(session.Playback.CanPlay); Assert.False(session.Playback.IsPlaying);
        Assert.Null(session.Playback.Program); Assert.Null(session.Playback.Frame); Assert.Equal(0, session.Playback.PositionSeconds);
        session.Playback.Play();
        Assert.False(session.Playback.IsPlaying);
    }

    private sealed class FailingImporter(CadDocument document) : ICadImporter
    {
        public bool Fail { get; set; }
        public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<CadImportResult>(new CadImportException("import failed")) : Task.FromResult(new CadImportResult(document));
    }

    private sealed class FakeImporter(CadDocument document) : ICadImporter
    { public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new CadImportResult(document)); }
}
