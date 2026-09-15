using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cad2Nc.Application;
using Cad2Nc.Core;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Tests;

public sealed class LncApplicationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-lnc-app-" + Guid.NewGuid().ToString("N"));
    public LncApplicationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string PathFor(string name) => Path.Combine(directory, name);
    private UserStore Store() => new(PathFor("settings"));
    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures", name));
    private static Contour Rectangle(string id, double x, double y, double w, double h, bool sheet = false) =>
        new(id, sheet ? "母板" : "零件", new GeometryFactory().CreatePolygon([
            new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h), new(x, y)
        ]), sheet);
    private CadDocument Drawing(bool autoSheet = true) => new(PathFor("stock.dxf"), "lnc-stock",
        [Rectangle("SHEET", 1000, 2000, 500, 500, autoSheet), Rectangle("P1", 1000, 2000, 100, 100), Rectangle("P2", 1250, 2250, 100, 100)], []);
    private sealed class Importer(CadDocument document) : ICadImporter
    {
        public Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new CadImportResult(document));
    }

    [Fact]
    public void StartupDefaultIsLncAndExplicitSaveIsIndependentOfLastConfigAndOpenSessions()
    {
        var store = Store();
        store.LastConfig = ConfigurationDefaults.Legacy() with { Tool = new() { Diameter = 12 } };
        store.Save();
        using var first = new WorkbenchSession(store);
        using var other = new WorkbenchSession(store);
        Assert.Equal(ConfigurationDefaults.Lnc(), first.Config);
        var customized = first.Config with { Machining = first.Config.Machining with { BoardThickness = 25, CuttingFeed = 7890 } };
        first.UpdateConfig(customized);
        Assert.Equal(ConfigurationDefaults.Lnc(), Store().StartupConfig);
        store.SaveStartupConfig(first.Config);
        Assert.Equal(ConfigurationDefaults.Lnc(), other.Config);
        using var restarted = new WorkbenchSession(Store());
        Assert.Equal(customized, restarted.Config);
        restarted.RestoreDefaults();
        Assert.Equal(ConfigurationDefaults.Lnc(), restarted.Config);
        Assert.Equal(customized, Store().StartupConfig);
        var json = JsonNode.Parse(File.ReadAllText(PathFor("settings/settings.json")))!;
        Assert.False(json["startupConfig"]!.AsObject().ContainsKey("workpiece"));
        json.AsObject().Remove("startupConfig");
        File.WriteAllText(PathFor("settings/settings.json"), json.ToJsonString());
        Assert.Equal(ConfigurationDefaults.Lnc(), Store().StartupConfig);
    }

    [Fact]
    public void FailedStartupWritePreservesSavedAndInMemoryDefaults()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store(); store.Save();
        var path = PathFor("settings/settings.json");
        var original = File.ReadAllText(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => store.SaveStartupConfig(ConfigurationDefaults.Legacy()));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(ConfigurationDefaults.Lnc(), store.StartupConfig);
            Assert.Equal(original, File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task AutoSheetOriginRemainsStableAcrossSelectionAndGeneratedOutputMatchesExport()
    {
        var document = Drawing();
        using var session = new WorkbenchSession(Store(), new Importer(document));
        await session.LoadCadAsync(document.SourcePath);
        Assert.Equal("SHEET", session.Workpiece.SheetContourId);
        Assert.False(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
        await session.GenerateAsync();
        Assert.Equal(new Point2(1000, 2000), session.Plan!.Origin);
        Assert.True(session.CanPlay);
        Assert.Null(session.PlaybackError);
        Assert.Contains("T2\r\n", session.NcCode);
        Assert.DoesNotContain('(', session.NcCode);
        Assert.DoesNotContain(')', session.NcCode);
        Assert.DoesNotContain('%', session.NcCode);
        Assert.DoesNotContain("G00 G43", session.NcCode);
        Assert.DoesNotContain("\n", session.NcCode.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.DoesNotContain("M06", session.NcCode);
        Assert.Contains("M17\r\nM30", session.NcCode);
        Assert.Contains(session.Plan.Toolpaths.SelectMany(p => p.Points), p => p.X < 0 || p.Y < 0);
        var output = PathFor("output.nc");
        await session.ExportNcAsync(output);
        var exported = File.ReadAllText(output);
        Assert.DoesNotContain('(', exported);
        Assert.DoesNotContain(')', exported);
        Assert.Equal(session.NcCode.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("(", StringComparison.Ordinal))
            .Select(line => line.TrimEnd('\r'))
            .Aggregate((left, right) => left + "\r\n" + right), exported);
        session.SetIncluded(["P1"], false);
        Assert.False(session.CanExport);
        await session.GenerateAsync();
        Assert.Equal(new Point2(1000, 2000), session.Plan!.Origin);
        Assert.Single(session.Plan.Toolpaths);
    }

    [Fact]
    public async Task ManualSheetAssignmentExcludesItInOneUndoAndDoesNotHideDeliberateInclusion()
    {
        var document = Drawing(autoSheet: false);
        using var session = new WorkbenchSession(Store(), new Importer(document));
        await session.LoadCadAsync(document.SourcePath);
        Assert.False(session.CanGenerate);
        Assert.Contains(nameof(WorkpieceSetup.SheetContourId), session.ValidationErrors.Keys);
        Assert.True(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
        session.UpdateWorkpiece(new() { SheetContourId = "SHEET" });
        Assert.False(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
        Assert.True(session.CanGenerate);
        session.Undo();
        Assert.Null(session.Workpiece.SheetContourId);
        Assert.True(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
        session.Redo();
        Assert.Equal("SHEET", session.Workpiece.SheetContourId);
        Assert.False(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
        session.SetIncluded(["SHEET"], true);
        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 9000 } });
        Assert.True(session.Contours.Single(c => c.Id == "SHEET").IsIncluded);
    }

    [Fact]
    public async Task WorkpieceAndFullConfigRoundTripWhileNewCadClearsTaskOriginAndPreservesProcess()
    {
        var document = Drawing(); File.WriteAllText(document.SourcePath, "fixture");
        using var first = new WorkbenchSession(Store(), new Importer(document));
        await first.LoadCadAsync(document.SourcePath);
        var config = first.Config with { OriginMode = OriginMode.Manual, Machining = first.Config.Machining with { BoardThickness = 20, LayeredCutting = true } };
        first.UpdateConfig(config);
        first.UpdateWorkpiece(first.Workpiece with { ManualOrigin = new(950, 1950) });
        var path = PathFor("manual.cad2nc.json");
        await first.SaveTaskAsync(path);
        Assert.False(first.IsDirty);
        Assert.Equal(3, TaskStorage.Read(path).SchemaVersion);
        using var reopened = new WorkbenchSession(Store(), new Importer(document));
        await reopened.LoadTaskAsync(path);
        Assert.Equal(config, reopened.Config);
        Assert.Equal(first.Workpiece, reopened.Workpiece);
        Assert.False(reopened.IsDirty);
        await reopened.GenerateAsync();
        var oldSetup = reopened.Workpiece;
        reopened.UpdateWorkpiece(oldSetup with { ManualOrigin = new(940, 1940) });
        Assert.True(reopened.HasStalePlan);
        Assert.True(reopened.IsDirty);
        Assert.False(reopened.CanExport);
        reopened.Undo();
        Assert.Equal(oldSetup, reopened.Workpiece);
        Assert.False(reopened.IsDirty);
        reopened.Redo();
        Assert.Equal(new Point2(940, 1940), reopened.Workpiece.ManualOrigin);
        await reopened.LoadCadAsync("next.dxf");
        Assert.Equal(config, reopened.Config);
        Assert.Null(reopened.Workpiece.ManualOrigin);
        Assert.Null(reopened.Workpiece.SheetContourId);
        Assert.False(reopened.CanGenerate);
    }

    [Fact]
    public void VersionOneTaskCannotAcquireBoardBottomSemanticsFromNewFields()
    {
        var document = Drawing();
        var path = PathFor("old.cad2nc.json");
        TaskStorage.Save(path, document, ConfigurationDefaults.Legacy(), document.DefaultSelection, new());
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["schemaVersion"] = 1;
        json["config"]!["machine"]!["profile"] = "LncRouter";
        json["config"]!["originMode"] = "SheetLowerLeft";
        json["config"]!["machining"]!["zReference"] = "WorkpieceBottom";
        json["config"]!["machining"]!["layeredCutting"] = false;
        json["workpiece"]!["sheetContourId"] = "SHEET";
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => TaskStorage.Read(path));
    }

    [Theory]
    [InlineData("config", "machine")]
    [InlineData("config", "originMode")]
    [InlineData("machining", "zReference")]
    [InlineData("machining", "boardThickness")]
    [InlineData("machining", "layeredCutting")]
    [InlineData("machine", "toolChange")]
    [InlineData("machine", "parkZ")]
    [InlineData("machine", "emitM17")]
    public void IncompleteVersionTwoTasksFailAndIncompleteStartupConfigFallsBackWithoutLosingSettings(string group, string field)
    {
        var document = Drawing();
        var path = PathFor("incomplete.cad2nc.json");
        TaskStorage.Save(path, document, ConfigurationDefaults.Lnc(), document.DefaultSelection, new());
        var task = JsonNode.Parse(File.ReadAllText(path))!;
        var config = task["config"]!;
        (group == "config" ? config : config[group])!.AsObject().Remove(field);
        File.WriteAllText(path, task.ToJsonString());
        Assert.Throws<InvalidDataException>(() => TaskStorage.Read(path));
        var store = Store(); store.Theme = "Dark"; store.LastView = new() { LeftWidth = 300 }; store.Save();
        var settingsPath = PathFor("settings/settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!;
        settings["startupConfig"] = config.DeepClone();
        File.WriteAllText(settingsPath, settings.ToJsonString());
        var reopened = Store();
        Assert.NotNull(reopened.LoadWarning);
        Assert.Equal(ConfigurationDefaults.Lnc(), reopened.StartupConfig);
        Assert.Equal("Dark", reopened.Theme);
        Assert.Equal(300, reopened.LastView.LeftWidth);
    }

    [Fact]
    public async Task ManualOriginDraftDoesNotGuessTheOtherAxisAndUndoRestoresDraftAcrossModeChanges()
    {
        var document = Drawing();
        using var session = new WorkbenchSession(Store(), new Importer(document));
        await session.LoadCadAsync(document.SourcePath);
        session.UpdateConfig(session.Config with { OriginMode = OriginMode.Manual });
        session.UpdateManualOriginCoordinate("ManualOriginX", 950);
        Assert.Null(session.Workpiece.ManualOrigin);
        Assert.True(session.TryGetRejectedNumber("ManualOriginX", out var x)); Assert.Equal(950, x);
        Assert.False(session.CanGenerate);
        session.UpdateConfig(session.Config with { OriginMode = OriginMode.SheetLowerLeft });
        Assert.True(session.CanGenerate);
        Assert.False(session.TryGetRejectedNumber("ManualOriginX", out _));
        session.Undo();
        Assert.Equal(OriginMode.Manual, session.Config.OriginMode);
        Assert.True(session.TryGetRejectedNumber("ManualOriginX", out x)); Assert.Equal(950, x);
        Assert.False(session.CanGenerate);
        session.Redo(); Assert.True(session.CanGenerate);
        session.Undo();
        session.UpdateManualOriginCoordinate("ManualOriginY", 1900);
        Assert.Equal(new Point2(950, 1900), session.Workpiece.ManualOrigin);
        Assert.True(session.CanGenerate);
        Assert.False(session.TryGetRejectedNumber("ManualOriginX", out _));
        session.Undo(); Assert.Null(session.Workpiece.ManualOrigin); Assert.False(session.CanGenerate);
        session.Redo();
        session.SetInputError("ManualOriginX", "请输入坐标", double.NaN);
        session.UpdateManualOriginCoordinate("ManualOriginY", 1800);
        Assert.Equal(new Point2(950, 1900), session.Workpiece.ManualOrigin);
        Assert.False(session.CanGenerate);
        session.UpdateManualOriginCoordinate("ManualOriginX", 900);
        Assert.Equal(new Point2(900, 1800), session.Workpiece.ManualOrigin);
        Assert.True(session.CanGenerate);
    }

    [Fact]
    public void PartialManualOriginInSavedTaskIsRejectedInsteadOfDefaultingMissingAxisToZero()
    {
        var document = Drawing();
        var path = PathFor("partial.cad2nc.json");
        TaskStorage.Save(path, document, ConfigurationDefaults.Lnc() with { OriginMode = OriginMode.Manual }, document.DefaultSelection, new(), new() { ManualOrigin = new(1000, 2000) });
        var task = JsonNode.Parse(File.ReadAllText(path))!;
        task["workpiece"]!["manualOrigin"]!.AsObject().Remove("y");
        File.WriteAllText(path, task.ToJsonString());
        Assert.Throws<InvalidDataException>(() => TaskStorage.Read(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"x\":1000}")]
    [InlineData("{\"y\":2000}")]
    [InlineData("{\"x\":1000,\"y\":2000,\"Y\":3000}")]
    [InlineData("{\"x\":1000,\"X\":900,\"y\":2000}")]
    [InlineData("{\"x\":1e400,\"y\":2000}")]
    [InlineData("{\"x\":1000,\"y\":\"NaN\"}")]
    public void ManualModeTasksRejectNullIncompleteDuplicateAndNonfiniteOrigin(string originJson)
    {
        var document = Drawing();
        var path = PathFor("invalid-origin.cad2nc.json");
        TaskStorage.Save(path, document, ConfigurationDefaults.Lnc() with { OriginMode = OriginMode.Manual }, document.DefaultSelection, new(), new() { ManualOrigin = new(1000, 2000) });
        var task = JsonNode.Parse(File.ReadAllText(path))!;
        task["workpiece"]!["manualOrigin"] = null;
        var malformed = task.ToJsonString().Replace("\"manualOrigin\":null", "\"manualOrigin\":" + originJson);
        File.WriteAllText(path, malformed);
        var error = Record.Exception(() => TaskStorage.Read(path));
        Assert.True(error is InvalidDataException or JsonException, $"Expected invalid task origin to be rejected; received {error}");
        Assert.Equal(malformed, File.ReadAllText(path));
    }

    [Fact]
    public void SavingManualModeWithoutCompleteCoordinatesPreservesExistingTask()
    {
        var document = Drawing();
        var path = PathFor("complete.cad2nc.json");
        var config = ConfigurationDefaults.Lnc() with { OriginMode = OriginMode.Manual };
        TaskStorage.Save(path, document, config, document.DefaultSelection, new(), new() { ManualOrigin = new(1000, 2000) });
        var previous = File.ReadAllText(path);
        Assert.Throws<ArgumentException>(() => TaskStorage.Save(path, document, config, document.DefaultSelection, new()));
        Assert.Throws<ArgumentException>(() => TaskStorage.Save(path, document, config, document.DefaultSelection, new(), new() { ManualOrigin = new(1000, double.NaN) }));
        Assert.Equal(previous, File.ReadAllText(path));
    }

    [Fact]
    public async Task StrictGeneratedParserFailureClearsResultAndPreventsExportOfAnyNc()
    {
        var document = Drawing();
        var rejectInitialPosition = false;
        var validationCalls = 0;
        using var session = new WorkbenchSession(Store(), new Importer(document), generatedParser: (code, config) =>
        {
            validationCalls++;
            // Exercise the real strict parser against a generated-program regression:
            // cutting begins before the first complete rapid XYZ location is established.
            if (!rejectInitialPosition) return NcPlaybackParser.ParseGenerated(code, config);
            var firstRapid = code.Split('\n').First(line => line.StartsWith("G00 X", StringComparison.Ordinal));
            var invalid = firstRapid.Replace("G00", "G01", StringComparison.Ordinal)
                .Replace(" Z45.000", "", StringComparison.Ordinal)
                .Replace(" F12000", " F1000", StringComparison.Ordinal);
            return NcPlaybackParser.ParseGenerated(code.Replace(firstRapid, invalid, StringComparison.Ordinal), config);
        });
        await session.LoadCadAsync(document.SourcePath);
        await session.GenerateAsync();
        Assert.True(session.CanExport);
        var previousPlan = session.Plan;
        var outputPath = PathFor("verified.nc");
        await session.ExportNcAsync(outputPath);
        var previousOutput = File.ReadAllText(outputPath);
        session.UpdateConfig(session.Config with { Machining = session.Config.Machining with { CuttingFeed = 9000 } });
        rejectInitialPosition = true;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.GenerateAsync());
        Assert.Contains("完整起始位置", error.Message);
        Assert.Equal(2, validationCalls);
        Assert.Equal(GenerationOutcome.Failed, session.GenerationOutcome);
        Assert.Same(previousPlan, session.Plan);
        Assert.True(session.HasStalePlan);
        Assert.Empty(session.NcCode);
        Assert.Empty(session.NcLines);
        Assert.False(session.CanPlay);
        Assert.False(session.CanExport);
        Assert.False(session.CanCopy);
        Assert.Contains(session.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("完整起始位置"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExportNcAsync(outputPath));
        Assert.Equal(previousOutput, File.ReadAllText(outputPath));
    }

    [Fact]
    public void CurrentPortableConfigRoundTripsEveryOption()
    {
        var lnc = ConfigurationDefaults.Lnc();
        var customized = lnc with
        {
            OriginMode = OriginMode.Manual,
            Machining = lnc.Machining with { BoardThickness = 22, LayeredCutting = true, EntryMode = EntryMode.Vertical, RampLength = 47, RampFeed = 2300 },
            Machine = lnc.Machine with { ToolChange = ToolChangeMode.ToolWithM06, UseProgramEnvelope = true, ParkX = -10, ParkY = 1500, ParkZ = 60, EmitM17 = false }
        };
        var path = ParameterConfigurationStorage.Save(PathFor("new"), "定制宝元", customized);
        Assert.Equal(customized, ParameterConfigurationStorage.Read(path).Config);
        Assert.Equal(30, ParameterConfigurationStorage.Compare(lnc, customized).Count);
    }

    [Fact]
    public async Task DefaultCliUsesLncFixtureAndManualCoordinatesOverrideOriginOnly()
    {
        var source = Fixture("lnc_sheet_mm.dxf");
        var target = PathFor("default.nc");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.True(await CommandLine.RunAsync(["--convert", source, "--output", target, "--accept-import-diagnostics"], output, error) == 0, error.ToString());
        Assert.Empty(error.ToString());
        var code = File.ReadAllText(target);
        Assert.DoesNotContain("M06", code);
        Assert.DoesNotContain("%", code);
        Assert.Contains("M17\r\nM30", code);
        using var summary = JsonDocument.Parse(output.ToString());
        Assert.Equal("LncRouter", summary.RootElement.GetProperty("machine_profile").GetString());
        Assert.Equal(1000, summary.RootElement.GetProperty("origin")[0].GetDouble());
        Assert.Equal(2000, summary.RootElement.GetProperty("origin")[1].GetDouble());
        Assert.Equal(17, summary.RootElement.GetProperty("surface_z").GetDouble());
        Assert.Equal(2, summary.RootElement.GetProperty("selected_contours").GetInt32());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await CommandLine.RunAsync(["--convert", source, "--output", target, "--origin", "manual", "--origin-x", "900", "--origin-y", "1900", "--accept-import-diagnostics"], output, error));
        using var manual = JsonDocument.Parse(output.ToString());
        Assert.Equal(900, manual.RootElement.GetProperty("origin")[0].GetDouble());
        Assert.Equal(1900, manual.RootElement.GetProperty("origin")[1].GetDouble());
    }

    [Theory]
    [InlineData("--origin", "manual")]
    [InlineData("--origin-x", "1")]
    [InlineData("--sheet-contour", "MISSING")]
    [InlineData("--profile", "invalid")]
    public async Task CliInvalidSetupPreservesPreviousOutput(string key, string value)
    {
        var target = PathFor("existing.nc"); File.WriteAllText(target, "PREVIOUS VERIFIED NC");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await CommandLine.RunAsync(["--convert", Fixture("lnc_sheet_mm.dxf"), "--output", target, key, value, "--accept-import-diagnostics"], output, error));
        Assert.Equal("PREVIOUS VERIFIED NC", File.ReadAllText(target));
        Assert.Empty(output.ToString());
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task CliPortableConfigRespectsCustomizedCuttingDepthAndRejectsProfileConflict()
    {
        var lnc = ConfigurationDefaults.Lnc();
        var configPath = ParameterConfigurationStorage.Save(PathFor("process"), "槽深参数", lnc with { Machining = lnc.Machining with { CuttingDepth = 3 } });
        var target = PathFor("config.nc");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.True(await CommandLine.RunAsync(["--convert", Fixture("lnc_sheet_mm.dxf"), "--output", target, "--config", configPath, "--accept-import-diagnostics"], output, error) == 0, error.ToString());
        Assert.Contains("Z3.000", File.ReadAllText(target));
        var previous = File.ReadAllText(target);
        Assert.Equal(1, await CommandLine.RunAsync(["--convert", Fixture("lnc_sheet_mm.dxf"), "--output", target, "--config", configPath, "--profile", "legacy"], output, error));
        Assert.Equal(previous, File.ReadAllText(target));
    }
}
