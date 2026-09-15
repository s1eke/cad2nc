using Cad2Nc.Application;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class NcSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-nc-session-" + Guid.NewGuid().ToString("N"));
    private const string Code = "%\r\nO1234\r\nG21 G17 G90 G94\r\nT3 M6\r\nS12000 M3\r\nG0 X0 Y0 Z5\r\nG1 Z-1 F200\r\nG1 X20 F800\r\nG0 Z5\r\nM30\r\n%\r\n";
    public NcSessionTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string Write(string name, string code = Code)
    {
        var path = Path.Combine(directory, name); File.WriteAllText(path, code); return path;
    }
    private CadDocument Drawing() => new(Path.Combine(directory, "drawing.dxf"), "fixture-hash",
        [new Contour("C0001", "板材", new GeometryFactory().CreatePolygon([new(0, 0), new(100, 0), new(100, 100), new(0, 100), new(0, 0)]))], [], false);
    private WorkbenchSession Session(Func<string, CancellationToken, Task<NcDocument>>? ncReader = null,
        Func<PlanningRequest, CancellationToken, Task<ToolpathPlan>>? planner = null)
    {
        var store = new UserStore(Path.Combine(directory, "settings"));
        store.SaveStartupConfig(ConfigurationDefaults.Legacy());
        return new(store, new Importer(Drawing()), planner, ncReader);
    }

    [Fact]
    public async Task OpenNcIsReadOnlyPreservesExactOriginalAndDoesNotApplyProgramParameters()
    {
        using var session = Session();
        session.ApplyPreset(new() { Tool = new() { ToolNumber = 7, Diameter = 12 }, Machining = new() { CuttingFeed = 9000 } });
        var config = session.Config;
        var path = Write("中文 程序.NC");
        await session.LoadNcAsync(path);
        Assert.True(session.IsNcDocument); Assert.NotNull(session.NcDocument); Assert.Null(session.Document);
        Assert.Equal(path, session.SourcePath); Assert.Equal("中文 程序.NC", session.FileName);
        Assert.Equal(Code, session.NcCode); Assert.Equal(session.NcDocument.Lines, session.NcLines);
        Assert.Same(config, session.Config); Assert.Empty(session.Contours); Assert.Null(session.Plan);
        Assert.True(session.CanCopy); Assert.True(session.CanPlay); Assert.False(session.CanGenerate);
        Assert.False(session.CanExport); Assert.False(session.CanSaveTask); Assert.False(session.IsDirty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveTaskAsync(Path.Combine(directory, "not-a-task.json")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportNcAsync(Path.Combine(directory, "not-generated.nc")));
    }

    [Fact]
    public async Task LegacyMachineFooterOpensAsReadOnlyPlaybackAndKeepsUnknownFinalZ()
    {
        using var session = Session();
        var config = session.Config;
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "legacy_t6_101_a18.nc"));
        var original = await File.ReadAllTextAsync(path);

        await session.LoadNcAsync(path);

        Assert.True(session.IsNcDocument);
        Assert.True(session.CanPlay); Assert.True(session.CanCopy);
        Assert.False(session.CanGenerate); Assert.False(session.CanExport); Assert.False(session.CanSaveTask);
        Assert.Null(session.PlaybackError);
        Assert.Equal(original, session.NcCode);
        Assert.Same(config, session.Config);
        session.Playback.Play();
        Assert.True(session.Playback.IsPlaying);
        session.Playback.Advance(TimeSpan.FromHours(24));
        Assert.False(session.Playback.IsPlaying);
        Assert.Equal(1, session.Playback.Progress);
        Assert.Equal(250, session.Playback.Frame!.Motion.LineIndex);
        Assert.Equal(new NcPosition(200, 2600, 0, false), session.Playback.Frame.Position);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnsupportedProgramKeepsTextAndParametersButClearsOldPlayback()
    {
        using var session = Session(); await session.LoadNcAsync(Write("valid.nc"));
        session.Playback.Play();
        const string unsupported = "O987\nT4 M6\nS6000 M3\nG0 X0 Y0 Z5\nG81 X10 Y10 Z-5 R2 F100\nM30\n";
        await session.LoadNcAsync(Write("cycle.nc", unsupported));
        Assert.True(session.IsNcDocument); Assert.Equal(unsupported, session.NcCode); Assert.True(session.CanCopy);
        Assert.False(session.CanPlay); Assert.False(session.Playback.IsPlaying); Assert.Null(session.Playback.Program);
        Assert.NotNull(session.PlaybackError); Assert.NotEmpty(session.NcDocument!.Parameters); Assert.NotEmpty(session.NcDocument.Notices);
    }

    [Fact]
    public async Task FailedReadRetainsCadResultAndItsExportEligibility()
    {
        using var session = Session(); await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        var document = session.Document; var plan = session.Plan; var code = session.NcCode; var revision = session.Revision;
        session.Playback.Play();
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.LoadNcAsync(Path.Combine(directory, "missing.nc")));
        Assert.Same(document, session.Document); Assert.Same(plan, session.Plan); Assert.Equal(code, session.NcCode);
        Assert.Equal(revision, session.Revision); Assert.True(session.CanExport); Assert.True(session.CanCopy);
        Assert.False(session.IsNcDocument); Assert.False(session.IsBusy); Assert.False(session.Playback.IsPlaying);
    }

    [Fact]
    public async Task FailedReadRetainsPreviousNcDocument()
    {
        using var session = Session(); await session.LoadNcAsync(Write("first.nc"));
        var document = session.NcDocument; var playback = session.Playback.Program;
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.LoadNcAsync(Path.Combine(directory, "missing.nc")));
        Assert.Same(document, session.NcDocument); Assert.Same(playback, session.Playback.Program);
        Assert.Equal(Code, session.NcCode); Assert.True(session.CanCopy); Assert.True(session.CanPlay);
    }

    [Fact]
    public async Task CancelledNonCooperativeReadCannotReplaceCurrentDocument()
    {
        var complete = new TaskCompletionSource<NcDocument>();
        using var session = Session((_, _) => complete.Task);
        await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        var drawing = session.Document; var plan = session.Plan;
        var opening = session.LoadNcAsync("pending.nc");
        Assert.True(session.IsBusy); Assert.False(session.CanCopy); session.Cancel();
        complete.SetResult(NcDocumentReader.Parse(Code, "pending.nc")); await opening;
        Assert.Same(drawing, session.Document); Assert.Same(plan, session.Plan); Assert.False(session.IsNcDocument);
        Assert.False(session.IsBusy); Assert.True(session.CanExport); Assert.True(session.CanPlay);
    }

    [Fact]
    public async Task LaterOpenWinsEvenWhenFirstReaderIgnoresCancellation()
    {
        var complete = new TaskCompletionSource<NcDocument>();
        using var session = Session((path, _) => path == "first.nc" ? complete.Task : Task.FromResult(NcDocumentReader.Parse(Code, path)));
        var first = session.LoadNcAsync("first.nc");
        await session.LoadNcAsync("second.nc");
        complete.SetResult(NcDocumentReader.Parse("O99\n", "first.nc")); await first;
        Assert.Equal("second.nc", session.SourcePath); Assert.Equal(Code, session.NcCode);
        Assert.True(session.CanPlay); Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task OpeningCadDiscardsPendingNcReadAndClearsPreviousNcState()
    {
        var complete = new TaskCompletionSource<NcDocument>();
        using var session = Session((_, _) => complete.Task);
        var opening = session.LoadNcAsync("pending.nc"); await session.LoadCadAsync("fixture.dxf");
        complete.SetResult(NcDocumentReader.Parse(Code, "pending.nc")); await opening;
        Assert.NotNull(session.Document); Assert.False(session.IsNcDocument); Assert.Null(session.NcDocument);
        Assert.Empty(session.NcCode); Assert.Empty(session.NcLines); Assert.Null(session.Playback.Program);
        Assert.True(session.CanGenerate); Assert.False(session.CanCopy); Assert.False(session.CanExport);
    }

    [Fact]
    public async Task OpeningNcDiscardsLateCadGeneration()
    {
        var complete = new TaskCompletionSource<ToolpathPlan>(); PlanningRequest? pending = null;
        using var session = Session(planner: (request, _) => { pending = request; return complete.Task; });
        await session.LoadCadAsync("fixture.dxf"); var generation = session.GenerateAsync();
        await session.LoadNcAsync(Write("opened.nc"));
        complete.SetResult(ToolpathPlanner.Generate(pending!)); await generation;
        Assert.True(session.IsNcDocument); Assert.Equal(Code, session.NcCode); Assert.Null(session.Plan);
        Assert.True(session.CanPlay); Assert.False(session.CanExport); Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task SoftwareParameterImportDoesNotChangeOpenedNcOrItsPlayback()
    {
        using var session = Session(); await session.LoadNcAsync(Write("opened.nc"));
        var document = session.NcDocument; var playback = session.Playback.Program;
        session.ApplyPreset(new() { Tool = new() { Diameter = 12 } });
        Assert.True(session.CanUndo); session.Undo();
        Assert.Same(document, session.NcDocument); Assert.Same(playback, session.Playback.Program);
        Assert.Equal(Code, session.NcCode); Assert.True(session.CanCopy); Assert.True(session.CanPlay);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ExplicitParameterImportAndResetInvalidateEvenWhenValuesAreUnchanged()
    {
        using var session = Session(); await session.LoadCadAsync("fixture.dxf"); await session.GenerateAsync();
        session.ApplyPreset(session.Config);
        Assert.False(session.CanExport); Assert.False(session.CanCopy); Assert.True(session.HasStalePlan);
        await session.GenerateAsync(); session.RestoreDefaults();
        Assert.False(session.CanExport); Assert.False(session.CanPlay); Assert.True(session.HasStalePlan);
    }

    private sealed class Importer(CadDocument document) : ICadImporter
    {
        public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new CadImportResult(document));
    }
}
