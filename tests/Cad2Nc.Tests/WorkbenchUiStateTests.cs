using Cad2Nc.Application;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class WorkbenchUiStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-workbench-state-" + Guid.NewGuid().ToString("N"));

    public WorkbenchUiStateTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void EmptyStateHasNoPrimaryCommandAndCompactsDiagnosticOccurrences()
    {
        using var session = Session();

        var state = WorkbenchUiState.Create(session, 2, 745, DiagnosticSeverity.Warning);

        Assert.Equal(WorkbenchPhase.Empty, state.Phase);
        Assert.Equal(WorkbenchPrimaryCommand.None, state.PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Diagnostics, state.ResultRoute);
        Assert.Equal("生成刀路", state.GenerateLabel);
        Assert.Equal("2 类提示", state.DiagnosticBadgeText);
        Assert.Equal(745, state.DiagnosticOccurrenceCount);
        Assert.True(state.HasDiagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, state.HighestDiagnosticSeverity);
    }

    [Fact]
    public async Task FileOpenAndInvalidInputsHaveDistinctStates()
    {
        var pending = new TaskCompletionSource<CadImportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(new DelegateImporter((_, _) => pending.Task));

        var opening = session.LoadCadAsync("pending.dxf");
        Assert.Equal(WorkbenchPhase.Loading, State(session).Phase);
        Assert.Equal(WorkbenchPrimaryCommand.None, State(session).PrimaryCommand);

        pending.SetResult(new CadImportResult(Document()));
        await opening;
        Assert.Equal(WorkbenchPhase.ReadyToGenerate, State(session).Phase);
        Assert.Equal("生成刀路", State(session).GenerateLabel);
        Assert.Equal(WorkbenchPrimaryCommand.Generate, State(session).PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Summary, State(session).ResultRoute);

        session.SetInputError("Diameter", "数字无效");
        var invalid = State(session);
        Assert.Equal(WorkbenchPhase.NeedsInput, invalid.Phase);
        Assert.Equal("1 项参数有误", invalid.ResultText);
        Assert.Equal(WorkbenchPrimaryCommand.None, invalid.PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Summary, invalid.ResultRoute);

        session.SetInputError("Diameter", null);
        session.SetIncluded(session.Contours.Select(contour => contour.Id), false);
        var unselected = State(session);
        Assert.Equal(WorkbenchPhase.NeedsInput, unselected.Phase);
        Assert.Equal("尚未选择轮廓", unselected.ResultText);
        Assert.Equal(WorkbenchPrimaryCommand.None, unselected.PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Summary, unselected.ResultRoute);
    }

    [Fact]
    public async Task GenerateUpdateAndRegenerateLabelsFollowTheResultLifecycle()
    {
        PlanningRequest? request = null;
        var pending = new TaskCompletionSource<ToolpathPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = Session(planner: (value, _) =>
        {
            request = value;
            return pending.Task;
        });
        await session.LoadCadAsync("fixture.dxf");

        var initial = State(session);
        Assert.Equal(WorkbenchPhase.ReadyToGenerate, initial.Phase);
        Assert.Equal("生成刀路", initial.GenerateLabel);
        Assert.Equal(WorkbenchPrimaryCommand.Generate, initial.PrimaryCommand);

        var generation = session.GenerateAsync();
        var running = State(session);
        Assert.Equal(GenerationOutcome.Running, session.GenerationOutcome);
        Assert.Equal(WorkbenchPhase.Generating, running.Phase);
        Assert.Equal("正在生成…", running.GenerateLabel);
        Assert.Equal(WorkbenchPrimaryCommand.None, running.PrimaryCommand);

        pending.SetResult(ToolpathPlanner.Generate(request!));
        await generation;
        var ready = State(session);
        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);
        Assert.Equal(WorkbenchPhase.ReadyToSave, ready.Phase);
        Assert.Equal("重新生成", ready.GenerateLabel);
        Assert.Equal(WorkbenchPrimaryCommand.Export, ready.PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Summary, ready.ResultRoute);

        session.UpdateConfig(session.Config with
        {
            Machining = session.Config.Machining with { CuttingFeed = session.Config.Machining.CuttingFeed - 500 }
        });
        var stale = State(session);
        Assert.Equal(GenerationOutcome.Superseded, session.GenerationOutcome);
        Assert.Equal(WorkbenchPhase.Stale, stale.Phase);
        Assert.Equal("更新刀路", stale.GenerateLabel);
        Assert.Equal(WorkbenchPrimaryCommand.Generate, stale.PrimaryCommand);
    }

    [Fact]
    public async Task FailedAndCanceledGenerationOfferRegenerationWithoutExportEmphasis()
    {
        using (var failed = Session(planner: (_, _) => Task.FromException<ToolpathPlan>(
                   new ToolpathGenerationException("刀路失败"))))
        {
            await failed.LoadCadAsync("failed.dxf");
            await Assert.ThrowsAsync<ToolpathGenerationException>(() => failed.GenerateAsync());
            var state = WorkbenchUiState.Create(failed, 1, 1, DiagnosticSeverity.Error);
            Assert.Equal(GenerationOutcome.Failed, failed.GenerationOutcome);
            Assert.Equal(WorkbenchPhase.GenerationFailed, state.Phase);
            Assert.Equal("重新生成", state.GenerateLabel);
            Assert.Equal(WorkbenchPrimaryCommand.Generate, state.PrimaryCommand);
            Assert.Equal(WorkbenchResultRoute.Diagnostics, state.ResultRoute);
        }

        using (var canceled = Session(planner: async (_, token) =>
               {
                   await Task.Delay(Timeout.InfiniteTimeSpan, token);
                   throw new InvalidOperationException("unreachable");
               }))
        {
            await canceled.LoadCadAsync("canceled.dxf");
            var generation = canceled.GenerateAsync();
            canceled.Cancel();
            await generation;
            var state = State(canceled);
            Assert.Equal(GenerationOutcome.Canceled, canceled.GenerationOutcome);
            Assert.Equal(WorkbenchPhase.ReadyToGenerate, state.Phase);
            Assert.Equal("重新生成", state.GenerateLabel);
            Assert.Equal(WorkbenchPrimaryCommand.Generate, state.PrimaryCommand);
        }
    }

    [Theory]
    [InlineData(DiagnosticSeverity.Warning, true, WorkbenchPhase.ReadyToSave, WorkbenchPrimaryCommand.Export, "2 类提示")]
    [InlineData(DiagnosticSeverity.Error, false, WorkbenchPhase.GenerationFailed, WorkbenchPrimaryCommand.Generate, "2 类错误")]
    public async Task DiagnosticSeverityControlsExportEligibilityAndPrimaryCommand(
        DiagnosticSeverity severity,
        bool canExport,
        WorkbenchPhase expectedPhase,
        WorkbenchPrimaryCommand expectedCommand,
        string expectedBadge)
    {
        using var session = Session(planner: (request, _) => Task.FromResult(ToolpathPlanner.Generate(request) with
        {
            Diagnostics = [new("matrix.diagnostic", "状态矩阵诊断", severity)]
        }));
        await session.LoadCadAsync("diagnostic.dxf");
        await session.GenerateAsync();

        var state = WorkbenchUiState.Create(session, 2, 745, severity);

        Assert.Equal(GenerationOutcome.Succeeded, session.GenerationOutcome);
        Assert.Equal(canExport, session.CanExport);
        Assert.Equal(expectedPhase, state.Phase);
        Assert.Equal(expectedCommand, state.PrimaryCommand);
        Assert.Equal(WorkbenchResultRoute.Diagnostics, state.ResultRoute);
        Assert.Equal(expectedBadge, state.DiagnosticBadgeText);
        Assert.Equal(745, state.DiagnosticOccurrenceCount);
        Assert.Equal("重新生成", state.GenerateLabel);
    }

    [Fact]
    public async Task ExternalNcDistinguishesPlayableAndTextOnlyPrograms()
    {
        const string playableCode = "%\r\nO1234\r\nG21 G17 G90 G94\r\nT3 M6\r\nS12000 M3\r\nG0 X0 Y0 Z5\r\nG1 Z-1 F200\r\nG1 X20 F800\r\nG0 Z5\r\nM30\r\n%\r\n";
        const string textOnlyCode = "O987\nT4 M6\nS6000 M3\nG0 X0 Y0 Z5\nG81 X10 Y10 Z-5 R2 F100\nM30\n";
        var playablePath = Write("playable.nc", playableCode);
        var textOnlyPath = Write("text-only.nc", textOnlyCode);

        using (var playable = Session())
        {
            await playable.LoadNcAsync(playablePath);
            Assert.True(playable.CanPlay);
            Assert.Equal(WorkbenchPhase.ExternalPlayable, State(playable).Phase);
            Assert.Equal(WorkbenchPrimaryCommand.None, State(playable).PrimaryCommand);
            Assert.Equal(WorkbenchResultRoute.Code, State(playable).ResultRoute);
        }

        using (var textOnly = Session())
        {
            await textOnly.LoadNcAsync(textOnlyPath);
            Assert.False(textOnly.CanPlay);
            Assert.NotNull(textOnly.PlaybackError);
            Assert.Equal(WorkbenchPhase.ExternalTextOnly, State(textOnly).Phase);
            Assert.Equal(WorkbenchPrimaryCommand.None, State(textOnly).PrimaryCommand);
            Assert.Equal(WorkbenchResultRoute.Code, State(textOnly).ResultRoute);
        }
    }

    private WorkbenchUiState State(WorkbenchSession session) => WorkbenchUiState.Create(session, 0, 0, null);

    private WorkbenchSession Session(
        ICadImporter? importer = null,
        Func<PlanningRequest, CancellationToken, Task<ToolpathPlan>>? planner = null,
        Func<string, CancellationToken, Task<NcDocument>>? ncReader = null)
    {
        var store = new UserStore(Path.Combine(directory, "settings"));
        store.SaveStartupConfig(ConfigurationDefaults.Legacy());
        return new(store, importer ?? new DelegateImporter((_, _) => Task.FromResult(new CadImportResult(Document()))), planner, ncReader);
    }

    private CadDocument Document() => new(Path.Combine(directory, "drawing.dxf"), "workbench-state-fixture",
        [new Contour("C0001", "板材", new GeometryFactory().CreatePolygon([
            new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0)
        ]))], [], false);

    private string Write(string name, string code)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, code);
        return path;
    }

    private sealed class DelegateImporter(Func<string, CancellationToken, Task<CadImportResult>> load) : ICadImporter
    {
        public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            load(path, cancellationToken);
    }
}
