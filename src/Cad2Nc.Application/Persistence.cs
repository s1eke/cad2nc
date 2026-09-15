using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

public sealed record ViewState
{
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double Scale { get; init; } = 1;
    public bool HasViewport { get; init; }
    public bool ShowSource { get; init; } = true;
    public bool ShowFinished { get; init; } = true;
    public bool ShowToolpaths { get; init; } = true;
    public bool ShowRapid { get; init; } = true;
    public bool ShowDirections { get; init; } = true;
    public bool LeftPanelOpen { get; init; } = true;
    public bool RightPanelOpen { get; init; } = true;
    public bool InspectionPanelOpen { get; init; } = true;
    public double LeftWidth { get; init; } = 240;
    public double RightWidth { get; init; } = 320;
    public double ResultHeightRatio { get; init; } = 0.28;
    public string ActiveInspector { get; init; } = "Contours";
    public string Theme { get; init; } = "Default";
    public ViewState Sanitize() => this with
    {
        CenterX = double.IsFinite(CenterX) ? CenterX : 0,
        CenterY = double.IsFinite(CenterY) ? CenterY : 0,
        Scale = double.IsFinite(Scale) && Scale > 0 ? Math.Clamp(Scale, 1e-7, 1e7) : 1,
        LeftWidth = double.IsFinite(LeftWidth) ? Math.Clamp(LeftWidth, 240, 400) : 240,
        RightWidth = double.IsFinite(RightWidth) ? Math.Clamp(RightWidth, 320, 480) : 320,
        ResultHeightRatio = double.IsFinite(ResultHeightRatio) ? Math.Clamp(ResultHeightRatio, 0.22, 0.55) : 0.28,
        ActiveInspector = ActiveInspector is "Contours" or "Parameters" ? ActiveInspector : "Contours",
        Theme = Theme is "Light" or "Dark" ? Theme : "Default"
    };
}

public sealed record ParameterPreset(string Name, ConversionConfig Config, bool IsBuiltIn = false);
public sealed record SavedContour(string Id, string Fingerprint, bool Included, CutSide Side);
public sealed record TaskFile(int SchemaVersion, string SourcePath, string SourceHash,
    ConversionConfig Config, ImmutableArray<SavedContour> Contours, ViewState View, WorkpieceSetup Workpiece);

public sealed class SourceFileMissingException(string sourcePath) : IOException($"找不到任务引用的图纸：{sourcePath}")
{
    public string SourcePath { get; } = sourcePath;
}

public static class TaskStorage
{
    public const int SchemaVersion = 3;
    private static readonly string[] V3ViewFields =
    [
        "centerX", "centerY", "scale", "hasViewport", "showSource", "showFinished", "showToolpaths",
        "showRapid", "showDirections", "leftPanelOpen", "rightPanelOpen", "inspectionPanelOpen", "leftWidth",
        "rightWidth", "resultHeightRatio", "activeInspector", "theme"
    ];
    private static readonly string[] V3ContourFields = ["id", "fingerprint", "included", "side"];
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static TaskFile Read(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("任务文件超过 4 MB 上限");
        using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var properties = Object(json.RootElement, "任务");
        if (!properties.TryGetValue("schemaVersion", out var schemaElement))
            throw new InvalidDataException("任务缺少必填字段：schemaVersion");
        var version = Integer(schemaElement, "格式版本");
        if (version != SchemaVersion) throw new InvalidDataException("不支持此任务版本，请使用匹配的 CAD2NC 版本");
        var allowed = new[] { "schemaVersion", "sourcePath", "sourceHash", "config", "contours", "view", "workpiece" };
        ValidateKnownProperties(properties, allowed, "任务");
        foreach (var field in new[] { "schemaVersion", "sourcePath", "sourceHash", "config", "contours", "view" })
            if (!properties.ContainsKey(field)) throw new InvalidDataException($"任务缺少必填字段：{field}");
        if (!properties.ContainsKey("workpiece")) throw new InvalidDataException("任务缺少必填字段：workpiece");

        // Version three is a closed format.  System.Text.Json intentionally ignores
        // unknown nested members, so validate the view and contour records before
        // accepting the deserialized model.  Doing this before deserialization also
        // ensures malformed duplicate members consistently surface as data errors.
        ReadCompleteView(properties["view"]);
        ReadCompleteContours(properties["contours"]);
        var configElement = properties["config"];
        var config = ParameterConfigurationStorage.ReadCompleteConfig(configElement);
        var file = json.RootElement.Deserialize<TaskFile>(JsonOptions)
            ?? throw new InvalidDataException("任务文件为空");
        if (string.IsNullOrWhiteSpace(file.SourcePath) || string.IsNullOrWhiteSpace(file.SourceHash) || file.Contours.IsDefault || file.Contours.Length > 2500 || file.Config is null || file.View is null)
            throw new InvalidDataException("任务文件内容不完整");
        var workpiece = ReadCompleteWorkpiece(properties["workpiece"]);
        file = file with { Config = config, Workpiece = workpiece };
        if (file.Config.OriginMode == OriginMode.Manual && file.Workpiece.ManualOrigin is null)
            throw new InvalidDataException("手动原点模式必须完整指定 X、Y 坐标");
        file.Config.Validate();
        if (file.Workpiece.ManualOrigin is { } origin && (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)))
            throw new InvalidDataException("任务手动原点须为有限坐标");
        if (file.Contours.Any(c => c is null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Fingerprint) || !Enum.IsDefined(c.Side)) || file.Contours.Select(c => c.Id).Distinct().Count() != file.Contours.Length)
            throw new InvalidDataException("任务轮廓记录无效");
        return file with { View = file.View.Sanitize() };
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{label}须为完整对象");
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value)) throw new InvalidDataException($"{label}含重复字段：{property.Name}");
        return properties;
    }

    private static void ValidateKnownProperties(IReadOnlyDictionary<string, JsonElement> properties, IEnumerable<string> allowed, string label)
    {
        var known = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.Keys)
            if (!known.Contains(property)) throw new InvalidDataException($"{label}包含不支持的字段：{property}");
    }

    private static int Integer(JsonElement element, string label) => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
        ? value : throw new InvalidDataException($"{label}须为整数");

    private static void ReadCompleteView(JsonElement element)
    {
        var properties = Object(element, "任务视图");
        ValidateKnownProperties(properties, V3ViewFields, "任务视图");
        foreach (var field in V3ViewFields)
            if (!properties.ContainsKey(field)) throw new InvalidDataException($"任务视图缺少必填字段：{field}");

        foreach (var field in new[] { "centerX", "centerY", "scale", "leftWidth", "rightWidth", "resultHeightRatio" })
            FiniteNumber(properties[field], $"任务视图 {field}");
        foreach (var field in new[] { "hasViewport", "showSource", "showFinished", "showToolpaths", "showRapid", "showDirections", "leftPanelOpen", "rightPanelOpen", "inspectionPanelOpen" })
            Boolean(properties[field], $"任务视图 {field}");
        Text(properties["activeInspector"], "任务视图 activeInspector");
        Text(properties["theme"], "任务视图 theme");
    }

    private static void ReadCompleteContours(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new InvalidDataException("任务轮廓须为数组");
        var count = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (++count > 2500) throw new InvalidDataException("任务轮廓数量超过 2500 条上限");
            var properties = Object(item, "任务轮廓");
            ValidateKnownProperties(properties, V3ContourFields, "任务轮廓");
            foreach (var field in V3ContourFields)
                if (!properties.ContainsKey(field)) throw new InvalidDataException($"任务轮廓缺少必填字段：{field}");
            var id = Text(properties["id"], "任务轮廓 id");
            var fingerprint = Text(properties["fingerprint"], "任务轮廓 fingerprint");
            if (id.Length == 0 || fingerprint.Length == 0) throw new InvalidDataException("任务轮廓记录无效");
            Boolean(properties["included"], "任务轮廓 included");
            var side = Text(properties["side"], "任务轮廓 side");
            if (!Enum.GetNames<CutSide>().Contains(side, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("任务轮廓 side 无效");
        }
    }

    private static string Text(JsonElement element, string label) => element.ValueKind == JsonValueKind.String
        ? element.GetString()! : throw new InvalidDataException($"{label}须为文字");

    private static double FiniteNumber(JsonElement element, string label) => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value)
        ? value : throw new InvalidDataException($"{label}须为有限数值");

    private static bool Boolean(JsonElement element, string label) => element.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? element.GetBoolean() : throw new InvalidDataException($"{label}须为布尔值");

    private static WorkpieceSetup ReadCompleteWorkpiece(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("工件原点设置须为完整对象");
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value)) throw new InvalidDataException("工件原点设置含重复字段");
        if (properties.Count != 2 || !properties.TryGetValue("sheetContourId", out var sheetContourId) ||
            !properties.TryGetValue("manualOrigin", out var manual))
            throw new InvalidDataException("工件原点设置缺少字段或包含未知字段");
        if (sheetContourId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("母板轮廓编号须为文字或 null");
        if (manual.ValueKind != JsonValueKind.Null)
        {
            if (manual.ValueKind != JsonValueKind.Object) throw new InvalidDataException("手动原点须为完整坐标");
            var coordinates = manual.EnumerateObject().ToArray();
            if (coordinates.Length != 2 || coordinates.Count(p => p.Name.Equals("x", StringComparison.OrdinalIgnoreCase)) != 1 || coordinates.Count(p => p.Name.Equals("y", StringComparison.OrdinalIgnoreCase)) != 1 ||
                coordinates.Any(p => p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var number) || !double.IsFinite(number)))
                throw new InvalidDataException("手动原点必须完整指定有限的 X、Y");
        }
        return element.Deserialize<WorkpieceSetup>(JsonOptions) ?? throw new InvalidDataException("工件原点设置为空");
    }

    public static string ResolveSource(string taskPath, TaskFile task) =>
        Path.GetFullPath(task.SourcePath, Path.GetDirectoryName(Path.GetFullPath(taskPath))!);

    public static void Save(string path, CadDocument document, ConversionConfig config,
        ImmutableDictionary<string, ContourSelection> selection, ViewState view, WorkpieceSetup? workpiece = null)
    {
        config.Validate();
        if (config.OriginMode == OriginMode.Manual && workpiece?.ManualOrigin is null)
            throw new ArgumentException("手动原点模式必须完整指定 X、Y 坐标", nameof(workpiece));
        if (workpiece?.ManualOrigin is { } origin && (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)))
            throw new ArgumentException("手动原点须为有限坐标", nameof(workpiece));
        if (!path.EndsWith(".cad2nc.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("任务文件须使用 .cad2nc.json 扩展名");
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(document.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("任务文件不能覆盖源 CAD");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, document.SourcePath);
        var file = new TaskFile(SchemaVersion, relative, document.SourceHash, config,
            document.Contours.Select(c => new SavedContour(c.Id, c.Fingerprint, selection[c.Id].Included, selection[c.Id].Side)).ToImmutableArray(), view.Sanitize(), workpiece ?? new());
        AtomicFile.WriteText(path, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
    }
}

public sealed class UserStore
{
    public const string DefaultPresetName = "默认配置";
    public static ParameterPreset DefaultPreset { get; } = new(DefaultPresetName, ConfigurationDefaults.Lnc(), IsBuiltIn: true);
    private readonly string directory;
    private readonly string defaultTaskDirectory;
    private readonly string defaultNcDirectory;
    private double ncReviewWidth = 480;
    private readonly ObservableCollection<ParameterPreset> presets = [DefaultPreset];
    public ReadOnlyObservableCollection<ParameterPreset> Presets { get; }
    public ObservableCollection<string> RecentTasks { get; } = [];
    public ConversionConfig LastConfig { get; set; } = new();
    public ConversionConfig StartupConfig { get; private set; } = ConfigurationDefaults.Lnc();
    public ViewState LastView { get; set; } = new();
    // The read-only NC inspector has a local preference independent of CAD/task layout.
    public double NcReviewWidth
    {
        get => ncReviewWidth;
        set => ncReviewWidth = double.IsFinite(value) ? Math.Clamp(value, 360, 800) : 480;
    }
    public string Theme { get; set; } = "Default";
    public string? TaskDirectoryOverride { get; private set; }
    public string? NcDirectoryOverride { get; private set; }
    public string TaskDirectory => TaskDirectoryOverride ?? defaultTaskDirectory;
    public string NcDirectory => NcDirectoryOverride ?? defaultNcDirectory;
    public string? LoadWarning { get; private set; }
    private string SettingsPath => Path.Combine(directory, "settings.json");

    public UserStore(string? directory = null)
    {
        Presets = new(presets);
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAD2NC");
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(documents)) documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        defaultTaskDirectory = Path.Combine(documents, "CAD2NC", "任务");
        defaultNcDirectory = Path.Combine(documents, "CAD2NC", "NC");
        if (File.Exists(SettingsPath))
        {
            try
            {
                if (new FileInfo(SettingsPath).Length > 4 * 1024 * 1024) throw new InvalidDataException();
                using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || ReadSetting<int>(root, "schemaVersion") != 1) throw new InvalidDataException();
                if (TryGetSetting(root, "lastConfig", out var savedConfigElement))
                {
                    try
                    {
                        var parsedConfig = ParameterConfigurationStorage.ReadCompleteConfig(savedConfigElement);
                        LastConfig = parsedConfig;
                    }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
                    { LoadWarning = "上次使用的参数无效，已恢复默认参数；其他有效配置和最近记录已保留。"; }
                }
                if (TryGetSetting(root, "startupConfig", out var startupElement))
                {
                    try { StartupConfig = ParameterConfigurationStorage.ReadCompleteConfig(startupElement); }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
                    { LoadWarning ??= "启动默认参数无效，已恢复宝元内置值；其他设置保留。"; }
                }
                LastView = (ReadSetting<ViewState>(root, "lastView") ?? new()).Sanitize();
                NcReviewWidth = ReadSetting<double?>(root, "ncReviewWidth") ?? 480;
                Theme = (LastView with { Theme = ReadSetting<string>(root, "theme") ?? "Default" }).Sanitize().Theme;
                if (TryGetSetting(root, "presets", out var savedPresets) && savedPresets.ValueKind == JsonValueKind.Array)
                {
                    var validPresets = new List<ParameterPreset>();
                    foreach (var item in savedPresets.EnumerateArray().Take(100))
                    {
                        ParameterPreset? preset;
                        try
                        {
                            preset = item.Deserialize<ParameterPreset>(TaskStorage.JsonOptions);
                            if (preset is not null && TryGetSetting(item, "config", out var presetConfig))
                                preset = preset with { Config = ParameterConfigurationStorage.ReadCompleteConfig(presetConfig) };
                        }
                        catch (JsonException) { LoadWarning ??= "部分已保存配置无效，其他有效配置和最近记录已保留。"; continue; }
                        if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 80 || preset.Config is null || preset.Config.GetValidationErrors().Count != 0)
                        { LoadWarning ??= "部分已保存配置无效，其他有效配置和最近记录已保留。"; continue; }
                        validPresets.Add(preset);
                    }
                    foreach (var preset in validPresets)
                    {
                        var name = preset.Name.Trim();
                        if (name.Equals(DefaultPresetName, StringComparison.OrdinalIgnoreCase))
                        { LoadWarning ??= "已保存配置名称无效，已跳过。"; continue; }
                        if (!presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) presets.Add(new(name, preset.Config));
                    }
                }
                if (TryGetSetting(root, "recentTasks", out var recentTasks) && recentTasks.ValueKind == JsonValueKind.Array)
                    foreach (var recent in recentTasks.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!)
                        .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) RecentTasks.Add(recent);
                TaskDirectoryOverride = ReadDirectoryOverride(ReadSetting<string>(root, "taskDirectory"));
                NcDirectoryOverride = ReadDirectoryOverride(ReadSetting<string>(root, "ncDirectory"));
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException or InvalidDataException)
            { LoadWarning = "上次设置无法读取，已恢复默认参数；原设置文件保留。"; LastConfig = new(); StartupConfig = ConfigurationDefaults.Lnc(); LastView = new(); NcReviewWidth = 480; presets.Clear(); presets.Add(DefaultPreset); RecentTasks.Clear(); return; }
        }
    }

    public void TouchRecent(string path)
    {
        path = Path.GetFullPath(path);
        var existing = RecentTasks.FirstOrDefault(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentTasks.Remove(existing);
        RecentTasks.Insert(0, path);
        while (RecentTasks.Count > 12) RecentTasks.RemoveAt(RecentTasks.Count - 1);
    }
    public void SavePreset(string name, ConversionConfig config)
    {
        name = ParameterConfigurationStorage.ValidateName(name);
        RejectDefaultName(name);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        var previous = presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var replacement = new ParameterPreset(name, config);
        var candidate = presets.Where(p => !p.IsBuiltIn).Select(p => p == previous ? replacement : p).ToList();
        if (previous is null) candidate.Add(replacement);
        if (candidate.Count > 100) throw new InvalidOperationException("最多保存 100 个我的配置。");
        SaveSettings(TaskDirectoryOverride, NcDirectoryOverride, candidate);
        if (previous is not null) presets[presets.IndexOf(previous)] = replacement;
        else presets.Add(replacement);
    }
    public void RemovePreset(string name)
    {
        name = ParameterConfigurationStorage.ValidateName(name);
        RejectDefaultName(name);
        var preset = presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;
        var candidate = presets.Where(p => !p.IsBuiltIn && p != preset).ToArray();
        SaveSettings(TaskDirectoryOverride, NcDirectoryOverride, candidate);
        presets.Remove(preset);
    }
    public void Save()
        => SaveSettings(TaskDirectoryOverride, NcDirectoryOverride);

    public void SaveStartupConfig(ConversionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        SaveSettings(TaskDirectoryOverride, NcDirectoryOverride, startupConfig: config);
        StartupConfig = config;
    }

    public void UpdateTaskDirectory(string? directory)
    {
        var candidate = SavePaths.NormalizeDirectory(directory);
        SaveSettings(candidate, NcDirectoryOverride);
        TaskDirectoryOverride = candidate;
    }

    public void UpdateNcDirectory(string? directory)
    {
        var candidate = SavePaths.NormalizeDirectory(directory);
        SaveSettings(TaskDirectoryOverride, candidate);
        NcDirectoryOverride = candidate;
    }

    private void SaveSettings(string? taskDirectory, string? ncDirectory, IReadOnlyCollection<ParameterPreset>? candidatePresets = null, ConversionConfig? startupConfig = null)
    {
        var config = LastConfig.GetValidationErrors().Count == 0 ? LastConfig : new ConversionConfig();
        var data = new SettingsData(1, config, LastView.Sanitize(), Theme,
            (candidatePresets ?? presets.Where(p => !p.IsBuiltIn).ToArray()).ToArray(), RecentTasks.ToArray(), taskDirectory, ncDirectory, NcReviewWidth, startupConfig ?? StartupConfig);
        AtomicFile.WriteText(SettingsPath, JsonSerializer.Serialize(data, TaskStorage.JsonOptions), new UTF8Encoding(false));
    }

    private static void RejectDefaultName(string name)
    {
        if (name.Equals(DefaultPresetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("默认配置不能覆盖或删除，请另存为我的配置。");
    }

    private T? ReadSetting<T>(JsonElement root, string name)
    {
        if (!TryGetSetting(root, name, out var element) || element.ValueKind == JsonValueKind.Null) return default;
        try { return element.Deserialize<T>(TaskStorage.JsonOptions); }
        catch (JsonException) { LoadWarning ??= "部分上次设置无效，其他有效配置和最近记录已保留。"; return default; }
    }

    private static bool TryGetSetting(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private string? ReadDirectoryOverride(string? path)
    {
        try { return SavePaths.NormalizeDirectory(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            LoadWarning = "上次保存目录无效，已恢复默认目录。";
            return null;
        }
    }
    private sealed record SettingsData(int SchemaVersion, ConversionConfig LastConfig, ViewState LastView,
        string Theme, ParameterPreset[] Presets, string[] RecentTasks, string? TaskDirectory = null, string? NcDirectory = null,
        double NcReviewWidth = 480, ConversionConfig? StartupConfig = null);
}
