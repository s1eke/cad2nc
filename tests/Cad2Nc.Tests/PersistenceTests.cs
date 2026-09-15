using System.Text.Json.Nodes;
using Cad2Nc.Application;
using Cad2Nc.Core;

namespace Cad2Nc.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cad2nc-persistence-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsDirectory => Path.Combine(directory, "settings");
    private string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    private UserStore Open() => new(SettingsDirectory);
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public void ViewStateUsesWorkspaceDefaultsAndSanitizesPersistedLayout()
    {
        var defaults = new ViewState();
        Assert.Equal(240, defaults.LeftWidth);
        Assert.Equal(320, defaults.RightWidth);
        Assert.Equal(0.28, defaults.ResultHeightRatio);
        Assert.Equal("Contours", defaults.ActiveInspector);

        var minimum = new ViewState
        {
            LeftWidth = 1, RightWidth = 1, ResultHeightRatio = 0.01, ActiveInspector = "Results"
        }.Sanitize();
        Assert.Equal(240, minimum.LeftWidth);
        Assert.Equal(320, minimum.RightWidth);
        Assert.Equal(0.22, minimum.ResultHeightRatio);
        Assert.Equal("Contours", minimum.ActiveInspector);

        var maximum = new ViewState
        {
            LeftWidth = 900, RightWidth = 900, ResultHeightRatio = 0.9, ActiveInspector = "Parameters"
        }.Sanitize();
        Assert.Equal(400, maximum.LeftWidth);
        Assert.Equal(480, maximum.RightWidth);
        Assert.Equal(0.55, maximum.ResultHeightRatio);
        Assert.Equal("Parameters", maximum.ActiveInspector);

        var nonfinite = new ViewState
        {
            LeftWidth = double.NaN, RightWidth = double.PositiveInfinity,
            ResultHeightRatio = double.NegativeInfinity, ActiveInspector = null!
        }.Sanitize();
        Assert.Equal(240, nonfinite.LeftWidth);
        Assert.Equal(320, nonfinite.RightWidth);
        Assert.Equal(0.28, nonfinite.ResultHeightRatio);
        Assert.Equal("Contours", nonfinite.ActiveInspector);
    }

    [Fact]
    public void ExistingSettingsViewWithoutWorkspaceFieldsUsesCompatibleDefaults()
    {
        var store = Open();
        store.LastView = new()
        {
            LeftWidth = 380, RightWidth = 440, ResultHeightRatio = 0.47, ActiveInspector = "Parameters"
        };
        store.Save();
        var oldSettings = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        var oldView = oldSettings["lastView"]!.AsObject();
        oldView.Remove("resultHeightRatio");
        oldView.Remove("activeInspector");
        File.WriteAllText(SettingsPath, oldSettings.ToJsonString());

        var reopened = Open();
        Assert.Null(reopened.LoadWarning);
        Assert.Equal(380, reopened.LastView.LeftWidth);
        Assert.Equal(440, reopened.LastView.RightWidth);
        Assert.Equal(0.28, reopened.LastView.ResultHeightRatio);
        Assert.Equal("Contours", reopened.LastView.ActiveInspector);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(SettingsPath))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void SettingsConfigsMissingStabilityFieldDefaultToEnabled()
    {
        var store = Open();
        var config = ConfigurationDefaults.Lnc();
        store.LastConfig = config;
        store.SaveStartupConfig(config);
        store.SavePreset("旧配置", config);
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        data["lastConfig"]!["machining"]!.AsObject().Remove("prioritizePartStability");
        data["startupConfig"]!["machining"]!.AsObject().Remove("prioritizePartStability");
        data["presets"]![0]!["config"]!["machining"]!.AsObject().Remove("prioritizePartStability");
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var reopened = Open();
        Assert.NotNull(reopened.LoadWarning);
    }

    [Fact]
    public void SettingsConfigsExplicitlyDisabledStabilityRoundTrip()
    {
        var store = Open();
        var config = ConfigurationDefaults.Lnc() with
        {
            Machining = ConfigurationDefaults.Lnc().Machining with { PrioritizePartStability = false }
        };
        store.LastConfig = config;
        store.SaveStartupConfig(config);
        store.SavePreset("关闭稳定顺序", config);

        var reopened = Open();
        Assert.False(reopened.LastConfig.Machining.PrioritizePartStability);
        Assert.False(reopened.StartupConfig.Machining.PrioritizePartStability);
        Assert.False(Assert.Single(reopened.Presets, p => p.Name == "关闭稳定顺序").Config.Machining.PrioritizePartStability);
        reopened.Save();
        var persisted = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.False(persisted["lastConfig"]!["machining"]!["prioritizePartStability"]!.GetValue<bool>());
        Assert.False(persisted["startupConfig"]!["machining"]!["prioritizePartStability"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(280, 360, 540)]
    [InlineData(380, 440, 720)]
    [InlineData(240, 320, 480)]
    public void SavedCadAndNcWidthsRoundTripIndependently(double left, double right, double nc)
    {
        var store = Open();
        store.LastView = new() { LeftWidth = left, RightWidth = right, ActiveInspector = "Parameters" };
        store.NcReviewWidth = nc;
        store.Save();

        var reopened = Open();
        Assert.Null(reopened.LoadWarning);
        Assert.Equal(store.LastView, reopened.LastView);
        Assert.Equal(nc, reopened.NcReviewWidth);
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(1, data["schemaVersion"]!.GetValue<int>());
        Assert.Equal(nc, data["ncReviewWidth"]!.GetValue<double>());
        Assert.False(data["lastView"]!.AsObject().ContainsKey("ncReviewWidth"));
    }

    [Theory]
    [InlineData(100, 360)]
    [InlineData(360, 360)]
    [InlineData(612.5, 612.5)]
    [InlineData(800, 800)]
    [InlineData(900, 800)]
    [InlineData(double.NaN, 480)]
    [InlineData(double.PositiveInfinity, 480)]
    [InlineData(double.NegativeInfinity, 480)]
    public void NcReviewWidthSanitizesBeforeSaving(double requested, double expected)
    {
        var store = Open();
        Assert.Equal(480, store.NcReviewWidth);
        store.NcReviewWidth = requested;
        Assert.Equal(expected, store.NcReviewWidth);
        store.Save();
        Assert.Equal(expected, Open().NcReviewWidth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"wide\"")]
    [InlineData("\"NaN\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1e400")]
    public void MissingOrInvalidNcWidthFallsBackWithoutDiscardingOtherPreferences(string? widthJson)
    {
        var store = Open();
        store.NcReviewWidth = 640;
        store.LastView = new() { LeftWidth = 280, RightWidth = 360, ActiveInspector = "Parameters" };
        store.LastConfig = store.LastConfig with { Machining = store.LastConfig.Machining with { CuttingFeed = 7654 } };
        store.Theme = "Dark";
        store.TouchRecent(Path.Combine(directory, "现有任务.cad2nc.json"));
        store.SavePreset("保留的配置", store.LastConfig);
        store.UpdateTaskDirectory(Path.Combine(directory, "现有任务"));
        store.UpdateNcDirectory(Path.Combine(directory, "现有NC"));
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        if (widthJson is null) data.Remove("ncReviewWidth");
        else data["ncReviewWidth"] = JsonNode.Parse(widthJson);
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var reopened = Open();
        Assert.Equal(480, reopened.NcReviewWidth);
        Assert.Equal(store.LastView, reopened.LastView);
        Assert.Equal(store.LastConfig, reopened.LastConfig);
        Assert.Equal(store.Theme, reopened.Theme);
        Assert.Equal(store.Presets.ToArray(), reopened.Presets.ToArray());
        Assert.Equal(store.RecentTasks.ToArray(), reopened.RecentTasks.ToArray());
        Assert.Equal(store.TaskDirectory, reopened.TaskDirectory);
        Assert.Equal(store.NcDirectory, reopened.NcDirectory);
        if (widthJson is null or "null") Assert.Null(reopened.LoadWarning);
        reopened.Save();
        Assert.Equal(480, Open().NcReviewWidth);
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(SettingsPath))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void MissingSavedSidebarWidthsUseCompactDefaults()
    {
        var store = Open();
        store.LastView = new() { LeftWidth = 380, RightWidth = 440, ActiveInspector = "Parameters" };
        store.Save();
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        data["lastView"]!.AsObject().Remove("leftWidth");
        data["lastView"]!.AsObject().Remove("rightWidth");
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var reopened = Open();
        Assert.Null(reopened.LoadWarning);
        Assert.Equal(240, reopened.LastView.LeftWidth);
        Assert.Equal(320, reopened.LastView.RightWidth);
        Assert.Equal("Parameters", reopened.LastView.ActiveInspector);
    }

    [Fact]
    public void DefaultsUseWindowsDocumentsWithoutCreatingDirectories()
    {
        var store = Open();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(documents)) documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        Assert.Equal(Path.Combine(documents, "CAD2NC", "任务"), store.TaskDirectory);
        Assert.Equal(Path.Combine(documents, "CAD2NC", "NC"), store.NcDirectory);
        Assert.Null(store.TaskDirectoryOverride);
        Assert.Null(store.NcDirectoryOverride);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DirectoryChangesAndResetsPersistImmediatelyAndIndependently()
    {
        var store = Open();
        var defaultTaskDirectory = store.TaskDirectory;
        var defaultNcDirectory = store.NcDirectory;
        var taskDirectory = Path.Combine(directory, "输出 任务");
        var ncDirectory = Path.Combine(directory, "输出 NC");
        store.UpdateTaskDirectory(taskDirectory);
        store.UpdateNcDirectory(ncDirectory);
        var reopened = Open();
        Assert.Equal(taskDirectory, reopened.TaskDirectory);
        Assert.Equal(ncDirectory, reopened.NcDirectory);
        Assert.False(Directory.Exists(taskDirectory));
        Assert.False(Directory.Exists(ncDirectory));

        store.UpdateTaskDirectory(null);
        reopened = Open();
        Assert.Equal(defaultTaskDirectory, reopened.TaskDirectory);
        Assert.Null(reopened.TaskDirectoryOverride);
        Assert.Equal(ncDirectory, reopened.NcDirectory);
        store.UpdateNcDirectory(null);
        reopened = Open();
        Assert.Equal(defaultNcDirectory, reopened.NcDirectory);
        Assert.Null(reopened.NcDirectoryOverride);
    }

    [Fact]
    public void ExistingSettingsWithoutDirectoryFieldsRetainAllOtherPreferences()
    {
        var store = Open();
        store.LastConfig = store.LastConfig with { Machining = store.LastConfig.Machining with { CuttingFeed = 7654 } };
        store.Theme = "Dark";
        store.LastView = store.LastView with { LeftWidth = 380, ResultHeightRatio = 0.41, ActiveInspector = "Parameters" };
        store.TouchRecent(Path.Combine(directory, "旧任务.cad2nc.json"));
        store.SavePreset("现有预设", store.LastConfig);
        var oldSettings = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        oldSettings.Remove("taskDirectory");
        oldSettings.Remove("ncDirectory");
        File.WriteAllText(SettingsPath, oldSettings.ToJsonString());

        var reopened = Open();
        Assert.Null(reopened.LoadWarning);
        Assert.Equal(store.LastConfig, reopened.LastConfig);
        Assert.Equal(store.LastView, reopened.LastView);
        Assert.Equal(store.Theme, reopened.Theme);
        Assert.Equal(store.Presets.ToArray(), reopened.Presets.ToArray());
        Assert.Equal(store.RecentTasks.ToArray(), reopened.RecentTasks.ToArray());
        Assert.Null(reopened.TaskDirectoryOverride);
        Assert.Null(reopened.NcDirectoryOverride);
    }

    [Fact]
    public void InvalidSavedDirectoryFallsBackWithoutDiscardingValidPreferences()
    {
        var store = Open();
        store.LastConfig = store.LastConfig with { Machining = store.LastConfig.Machining with { CuttingFeed = 7654 } };
        var ncDirectory = Path.Combine(directory, "有效 NC");
        store.UpdateNcDirectory(ncDirectory);
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        data["taskDirectory"] = "relative-directory";
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var reopened = Open();
        Assert.NotNull(reopened.LoadWarning);
        Assert.Null(reopened.TaskDirectoryOverride);
        Assert.Equal(ncDirectory, reopened.NcDirectory);
        Assert.Equal(store.LastConfig, reopened.LastConfig);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void FailedDirectoryWriteKeepsMemoryAndSavedSettingsUnchanged(bool task, bool reset)
    {
        // The desktop app runs on Windows, where a file opened without delete sharing
        // prevents the atomic replacement and reliably exercises a real write failure.
        if (!OperatingSystem.IsWindows()) return;
        var store = Open();
        store.UpdateTaskDirectory(Path.Combine(directory, "原任务"));
        store.UpdateNcDirectory(Path.Combine(directory, "原NC"));
        var oldTask = store.TaskDirectory;
        var oldNc = store.NcDirectory;
        var original = File.ReadAllText(SettingsPath);
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var candidate = reset ? null : Path.Combine(directory, "新目录");
            var error = Record.Exception(() =>
            {
                if (task) store.UpdateTaskDirectory(candidate);
                else store.UpdateNcDirectory(candidate);
            });
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file access failure, got {error}");
            Assert.Equal(oldTask, store.TaskDirectory);
            Assert.Equal(oldNc, store.NcDirectory);
            Assert.Equal(original, File.ReadAllText(SettingsPath));
        }
        Assert.Equal(oldTask, Open().TaskDirectory);
        Assert.Equal(oldNc, Open().NcDirectory);
        Assert.Single(Directory.GetFiles(SettingsDirectory));
    }

    [Fact]
    public void BuiltInDefaultIsAlwaysPresentAndCannotBeOverwrittenOrDeleted()
    {
        var store = Open();
        var preset = Assert.Single(store.Presets);
        Assert.True(preset.IsBuiltIn);
        Assert.Equal(UserStore.DefaultPresetName, preset.Name);
        Assert.Equal(ConfigurationDefaults.Lnc(), preset.Config);
        Assert.Throws<InvalidOperationException>(() => store.SavePreset(" 默认配置 ", new()));
        Assert.Throws<InvalidOperationException>(() => store.RemovePreset(" 默认配置 "));
        Assert.Equal(preset, Assert.Single(store.Presets));
        Assert.False(Directory.Exists(directory));
        store.SavePreset("我的配置", new());
        store.RemovePreset("我的配置");
        Assert.Equal(preset, Assert.Single(Open().Presets));
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Empty(data["presets"]!.AsArray());
    }

    [Fact]
    public void HistoricalUserPresetNamedDefaultIsPreservedWithUniqueName()
    {
        var store = Open();
        var custom = new ConversionConfig { Tool = new() { ToolNumber = 3, LengthOffset = 10 } };
        store.SavePreset("旧名称", custom);
        store.SavePreset("默认配置（原有配置）", new());
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        data["presets"]![0]!["name"] = UserStore.DefaultPresetName;
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var loaded = Open();
        Assert.Equal(2, loaded.Presets.Count);
        Assert.Equal(ConfigurationDefaults.Lnc(), loaded.Presets[0].Config);
        Assert.True(loaded.Presets[0].IsBuiltIn);
        Assert.Contains(loaded.Presets, p => p.Name == "默认配置（原有配置）" && !p.IsBuiltIn);
        Assert.NotNull(loaded.LoadWarning);
        loaded.Save();
        Assert.Equal(loaded.Presets.ToArray(), Open().Presets.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLastConfigDoesNotDiscardOtherValidPreferences(bool malformedType)
    {
        var store = Open();
        store.SavePreset("保留的配置", new() { Tool = new() { LengthOffset = 12 } });
        store.TouchRecent(Path.Combine(directory, "保留的任务.cad2nc.json"));
        store.Theme = "Dark";
        store.LastView = new() { LeftWidth = 360 };
        store.UpdateNcDirectory(Path.Combine(directory, "原NC"));
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        if (malformedType) data["lastConfig"] = "无效的旧参数";
        else data["lastConfig"]!["tool"]!["diameter"] = -1;
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var loaded = Open();
        Assert.NotNull(loaded.LoadWarning);
        Assert.Equal(new ConversionConfig(), loaded.LastConfig);
        Assert.Equal(store.Presets.ToArray(), loaded.Presets.ToArray());
        Assert.Equal(store.RecentTasks.ToArray(), loaded.RecentTasks.ToArray());
        Assert.Equal(store.Theme, loaded.Theme);
        Assert.Equal(store.LastView, loaded.LastView);
        Assert.Equal(store.NcDirectory, loaded.NcDirectory);
    }

    [Fact]
    public void InvalidPresetDoesNotDiscardOtherPresetsOrRecentTasks()
    {
        var store = Open();
        store.SavePreset("有效配置", new());
        store.TouchRecent(Path.Combine(directory, "现有任务.cad2nc.json"));
        store.Save();
        var data = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        data["presets"]!.AsArray().Insert(0, JsonNode.Parse("{\"name\":\"损坏配置\",\"config\":{\"tool\":{\"toolNumber\":\"bad\"}}}"));
        File.WriteAllText(SettingsPath, data.ToJsonString());

        var loaded = Open();
        Assert.NotNull(loaded.LoadWarning);
        Assert.Equal(store.Presets.ToArray(), loaded.Presets.ToArray());
        Assert.Equal(store.RecentTasks.ToArray(), loaded.RecentTasks.ToArray());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("remove")]
    public void FailedPresetWritePreservesMemoryAndSettings(string operation)
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Open();
        store.SavePreset("原配置", new());
        var original = File.ReadAllText(SettingsPath);
        var presets = store.Presets.ToArray();
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() =>
            {
                if (operation == "remove") store.RemovePreset("原配置");
                else store.SavePreset(operation == "add" ? "新配置" : "原配置", new() { Tool = new() { LengthOffset = 9 } });
            });
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a file access failure, got {error}");
            Assert.Equal(presets, store.Presets.ToArray());
            Assert.Equal(original, File.ReadAllText(SettingsPath));
        }
        Assert.Equal(presets, Open().Presets.ToArray());
        Assert.Single(Directory.GetFiles(SettingsDirectory));
    }

    [Fact]
    public void ReplacementKeepsStableOrderInMemoryAndOnDisk()
    {
        var store = Open();
        store.SavePreset("第一套", new());
        store.SavePreset("第二套", new());
        store.SavePreset("第一套", new() { Tool = new() { LengthOffset = 15 } });
        Assert.Equal([UserStore.DefaultPresetName, "第一套", "第二套"], store.Presets.Select(p => p.Name));
        Assert.Equal(store.Presets.ToArray(), Open().Presets.ToArray());
    }
}
