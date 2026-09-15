using System.Globalization;
using System.Text;
using System.Text.Json;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

public sealed record ParameterComparison(string Key, string Label, string CurrentValue, string ImportedValue, bool IsChanged);

/// <summary>Portable parameter files and the versioned configuration reader shared by task files.</summary>
public static class ParameterConfigurationStorage
{
    public const string Extension = ".cad2nc.config.json";
    public const int SchemaVersion = 3;
    private const int MaximumBytes = 256 * 1024;

    private static readonly string[] CurrentToolFields =
        ["toolNumber", "lengthOffset", "toolType", "diameter", "spindleDirection"];
    private static readonly string[] CurrentMachiningFields =
        ["spindleSpeed", "cuttingFeed", "cuttingDepth", "cutDirection", "edgeAllowance", "safeZ", "plungeFeed",
         "maxStepDown", "leaveSkin", "boardThickness", "zReference", "layeredCutting", "prioritizePartStability", "entryMode", "rampLength", "rampFeed"];

    public static ParameterPreset Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("参数配置文件超过 256 KB 上限。");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("配置须为完整对象。");
            var versions = json.RootElement.EnumerateObject().Where(p => p.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (versions.Length != 1) throw new InvalidDataException("配置缺少或重复格式版本。");
            var version = Integer(versions[0].Value, "格式版本");
            if (version != SchemaVersion)
                throw new InvalidDataException("不支持此参数配置版本，请使用匹配的 CAD2NC 版本。");
            var root = Object(json.RootElement, "配置", "schemaVersion", "name", "tool", "machining", "machine", "originMode");
            var name = ValidateName(Text(root["name"], "配置名称"));
            var config = ReadCurrentConfigParts(root);
            config.Validate();
            config.Validate();
            return new ParameterPreset(name, config);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("参数配置文件无效：" + error.Message, error);
        }
    }

    public static string Save(string path, string name, ConversionConfig config)
    {
        name = ValidateName(name);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        path = NormalizePath(path);
        // Explicitly enumerate portable fields: future session or task fields must never leak into this format.
        var file = new
        {
            schemaVersion = SchemaVersion,
            name,
            config.OriginMode,
            machine = new { config.Machine.Profile, config.Machine.ToolChange, config.Machine.UseProgramEnvelope,
                config.Machine.ParkAtEnd, config.Machine.ParkX, config.Machine.ParkY, config.Machine.ParkZ, config.Machine.EmitM17 },
            tool = new { config.Tool.ToolNumber, config.Tool.LengthOffset, config.Tool.ToolType, config.Tool.Diameter, config.Tool.SpindleDirection },
            machining = new { config.Machining.SpindleSpeed, config.Machining.CuttingFeed, config.Machining.CuttingDepth,
                config.Machining.CutDirection, config.Machining.EdgeAllowance, config.Machining.SafeZ,
                config.Machining.PlungeFeed, config.Machining.MaxStepDown, config.Machining.BoardThickness,
                config.Machining.LeaveSkin, config.Machining.ZReference, config.Machining.LayeredCutting, config.Machining.PrioritizePartStability,
                config.Machining.EntryMode,
                config.Machining.RampLength, config.Machining.RampFeed }
        };
        var text = JsonSerializer.Serialize(file, TaskStorage.JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new ArgumentException("参数配置文件超过 256 KB 上限。");
        return AtomicFile.WriteText(path, text, new UTF8Encoding(false));
    }

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequireName(path);
        path = Path.GetFullPath(path);
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^5];
        if (path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        if (path.EndsWith(".cad2nc", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        RequireName(path);
        return path + Extension;
    }

    public static IReadOnlyList<ParameterComparison> Compare(ConversionConfig current, ConversionConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        var result = new List<ParameterComparison>();
        void Add(string key, string label, object before, object after, string unit = "")
        {
            string Format(object value) => (value switch
            {
                SpindleDirection spindle => spindle == SpindleDirection.Clockwise ? "正转（M03）" : "反转（M04）",
                CutDirection cut => cut == CutDirection.Counterclockwise ? "逆时针" : "顺时针",
                MachineProfile profile => profile == MachineProfile.LncRouter ? "宝元 LNC · 当前木工开料机" : "通用 Fanuc · 旧版",
                ZReference reference => reference == ZReference.WorkpieceBottom ? "板底／垫板上表面" : "板材上表面",
                OriginMode origin => origin switch { OriginMode.SheetLowerLeft => "母板左下角", OriginMode.Manual => "手动 CAD 坐标", _ => "刀心包围框左下角" },
                EntryMode entry => entry == EntryMode.Ramp ? "沿轮廓斜向下刀" : "垂直下刀",
                ToolChangeMode change => change == ToolChangeMode.ToolOnly ? "单独 T 刀号" : "T + M06",
                bool enabled => enabled ? "开启" : "关闭",
                double number => number.ToString("G", CultureInfo.InvariantCulture),
                IFormattable valueWithFormat => valueWithFormat.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            }) + unit;
            result.Add(new(key, label, Format(before), Format(after), !Equals(before, after)));
        }
        Add(nameof(ToolConfig.ToolNumber), "刀号 T", current.Tool.ToolNumber, candidate.Tool.ToolNumber);
        Add(nameof(ToolConfig.LengthOffset), "长度补偿 H", current.Tool.LengthOffset, candidate.Tool.LengthOffset);
        Add(nameof(ToolConfig.ToolType), "刀具名称", current.Tool.ToolType, candidate.Tool.ToolType);
        Add(nameof(ToolConfig.Diameter), "刀具直径", current.Tool.Diameter, candidate.Tool.Diameter, " mm");
        Add(nameof(ToolConfig.SpindleDirection), "主轴方向", current.Tool.SpindleDirection, candidate.Tool.SpindleDirection);
        Add(nameof(MachiningConfig.SpindleSpeed), "主轴转速", current.Machining.SpindleSpeed, candidate.Machining.SpindleSpeed, " rpm");
        Add(nameof(MachiningConfig.CuttingFeed), "切削进给", current.Machining.CuttingFeed, candidate.Machining.CuttingFeed, " mm/min");
        Add(nameof(MachiningConfig.PlungeFeed), "下刀进给", current.Machining.PlungeFeed, candidate.Machining.PlungeFeed, " mm/min");
        Add(nameof(MachiningConfig.CuttingDepth), "加工深度 Z", current.Machining.CuttingDepth, candidate.Machining.CuttingDepth, " mm");
        Add(nameof(MachiningConfig.SafeZ), "安全高度", current.Machining.SafeZ, candidate.Machining.SafeZ, " mm");
        Add(nameof(MachiningConfig.MaxStepDown), "单层下切", current.Machining.MaxStepDown, candidate.Machining.MaxStepDown, " mm");
        Add(nameof(MachiningConfig.LeaveSkin), "留皮厚度", current.Machining.LeaveSkin, candidate.Machining.LeaveSkin, " mm");
        Add(nameof(MachiningConfig.LayeredCutting), "分层加工", current.Machining.LayeredCutting, candidate.Machining.LayeredCutting);
        Add(nameof(MachiningConfig.PrioritizePartStability), "优先保持零件稳定", current.Machining.PrioritizePartStability, candidate.Machining.PrioritizePartStability);
        Add(nameof(MachiningConfig.EdgeAllowance), "封边吃料", current.Machining.EdgeAllowance, candidate.Machining.EdgeAllowance, " mm");
        Add(nameof(MachiningConfig.CutDirection), "走刀方向", current.Machining.CutDirection, candidate.Machining.CutDirection);
        Add(nameof(MachineConfig.Profile), "机床配置", current.Machine.Profile, candidate.Machine.Profile);
        Add(nameof(ConversionConfig.OriginMode), "XY 零点", current.OriginMode, candidate.OriginMode);
        Add(nameof(MachiningConfig.BoardThickness), "板厚", current.Machining.BoardThickness, candidate.Machining.BoardThickness, " mm");
         Add(nameof(MachiningConfig.ZReference), "Z 零点", current.Machining.ZReference, candidate.Machining.ZReference);
         Add(nameof(MachiningConfig.EntryMode), "下刀方式", current.Machining.EntryMode, candidate.Machining.EntryMode);
        Add(nameof(MachiningConfig.RampLength), "斜坡长度", current.Machining.RampLength, candidate.Machining.RampLength, " mm");
        Add(nameof(MachiningConfig.RampFeed), "斜坡进给", current.Machining.RampFeed, candidate.Machining.RampFeed, " mm/min");
        Add(nameof(MachineConfig.ToolChange), "换刀输出", current.Machine.ToolChange, candidate.Machine.ToolChange);
        Add(nameof(MachineConfig.UseProgramEnvelope), "程序外框", current.Machine.UseProgramEnvelope, candidate.Machine.UseProgramEnvelope);
        Add(nameof(MachineConfig.ParkAtEnd), "结束停车", current.Machine.ParkAtEnd, candidate.Machine.ParkAtEnd);
        Add(nameof(MachineConfig.ParkX), "停车 X", current.Machine.ParkX, candidate.Machine.ParkX, " mm");
        Add(nameof(MachineConfig.ParkY), "停车 Y", current.Machine.ParkY, candidate.Machine.ParkY, " mm");
        Add(nameof(MachineConfig.ParkZ), "停车 Z", current.Machine.ParkZ, candidate.Machine.ParkZ, " mm");
        Add(nameof(MachineConfig.EmitM17), "末尾 M17", current.Machine.EmitM17, candidate.Machine.EmitM17);
        return result.AsReadOnly();
    }

    internal static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("配置名称须为 1–80 个字符。");
        return name;
    }

    internal static ConversionConfig ReadCompleteConfig(JsonElement element)
    {
        var root = Object(element, "参数", "tool", "machining", "machine", "originMode");
        var config = ReadCurrentConfigParts(root);
        config.Validate();
        return config;
    }

    private static ConversionConfig ReadCurrentConfigParts(IReadOnlyDictionary<string, JsonElement> root)
    {
        var tool = Object(root["tool"], "刀具", CurrentToolFields);
        var machining = Object(root["machining"], "加工参数", CurrentMachiningFields);

        var config = new ConversionConfig
        {
            Tool = ParseTool(tool),
            Machining = new MachiningConfig
            {
                SpindleSpeed = Integer(machining["spindleSpeed"], "主轴转速"),
                CuttingFeed = Number(machining["cuttingFeed"], "切削进给"),
                CuttingDepth = Number(machining["cuttingDepth"], "加工深度"),
                CutDirection = Direction<CutDirection>(machining["cutDirection"], "走刀方向"),
                EdgeAllowance = Number(machining["edgeAllowance"], "封边吃料"),
                SafeZ = Number(machining["safeZ"], "安全高度"),
                PlungeFeed = Number(machining["plungeFeed"], "下刀进给"),
                MaxStepDown = machining.TryGetValue("maxStepDown", out var maxStepDown) ? Number(maxStepDown, "单层下切") : 3,
                LeaveSkin = Number(machining["leaveSkin"], "留皮厚度"),
                BoardThickness = Number(machining["boardThickness"], "板厚"),
                ZReference = Direction<ZReference>(machining["zReference"], "Z 零点"),
                LayeredCutting = Boolean(machining["layeredCutting"], "分层加工"),
                PrioritizePartStability = Boolean(machining["prioritizePartStability"], "优先保持零件稳定"),
                EntryMode = Direction<EntryMode>(machining["entryMode"], "下刀方式"),
                RampLength = Number(machining["rampLength"], "斜坡长度"),
                RampFeed = Number(machining["rampFeed"], "斜坡进给")
            },
            OriginMode = Direction<OriginMode>(root["originMode"], "XY 零点")
        };
        var machine = Object(root["machine"], "机床", "profile", "toolChange", "useProgramEnvelope", "parkAtEnd", "parkX", "parkY", "parkZ", "emitM17");
        return config with
        {
            Machine = new MachineConfig
            {
                Profile = Direction<MachineProfile>(machine["profile"], "机床配置"),
                ToolChange = Direction<ToolChangeMode>(machine["toolChange"], "换刀输出"),
                UseProgramEnvelope = Boolean(machine["useProgramEnvelope"], "程序外框"),
                ParkAtEnd = Boolean(machine["parkAtEnd"], "结束停车"),
                ParkX = Number(machine["parkX"], "停车 X"),
                ParkY = Number(machine["parkY"], "停车 Y"),
                ParkZ = Number(machine["parkZ"], "停车 Z"),
                EmitM17 = Boolean(machine["emitM17"], "末尾 M17")
            }
        };
    }

    private static ToolConfig ParseTool(IReadOnlyDictionary<string, JsonElement> tool) => new()
    {
        ToolNumber = Integer(tool["toolNumber"], "刀号 T"),
        LengthOffset = Integer(tool["lengthOffset"], "长度补偿 H"),
        ToolType = Text(tool["toolType"], "刀具名称"),
        Diameter = Number(tool["diameter"], "刀具直径"),
        SpindleDirection = Direction<SpindleDirection>(tool["spindleDirection"], "主轴方向")
    };


    private static void RequireName(string path)
    {
        if (string.IsNullOrWhiteSpace(Path.GetFileName(path))) throw new ArgumentException("配置文件名不能为空。", nameof(path));
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string label, params string[] required)
        => Object(element, label, required, [], allowUnknown: false);

    private static Dictionary<string, JsonElement> Object(JsonElement element, string label, string[] required, bool allowUnknown)
        => Object(element, label, required, [], allowUnknown);

    private static Dictionary<string, JsonElement> Object(JsonElement element, string label, string[] required, string[] optional)
        => Object(element, label, required, optional, allowUnknown: false);

    private static Dictionary<string, JsonElement> Object(JsonElement element, string label, string[] required, string[] optional, bool allowUnknown)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{label}须为完整对象。");
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateObject())
        {
            if (!allowUnknown && !required.Contains(item.Name, StringComparer.OrdinalIgnoreCase) && !optional.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label}包含不支持的字段：{item.Name}。");
            if (!properties.TryAdd(item.Name, item.Value)) throw new InvalidDataException($"{label}包含重复字段：{item.Name}。");
        }
        foreach (var field in required)
            if (!properties.ContainsKey(field)) throw new InvalidDataException($"{label}缺少必填字段：{field}。");
        return properties;
    }

    private static string Text(JsonElement element, string label) => element.ValueKind == JsonValueKind.String
        ? element.GetString()! : throw new InvalidDataException($"{label}须为文字。");
    private static int Integer(JsonElement element, string label) => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
        ? value : throw new InvalidDataException($"{label}须为整数。");
    private static double Number(JsonElement element, string label) => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value)
        ? value : throw new InvalidDataException($"{label}须为有限数值。");
    private static bool Boolean(JsonElement element, string label) => element.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? element.GetBoolean() : throw new InvalidDataException($"{label}须为布尔值。");
    private static T Direction<T>(JsonElement element, string label) where T : struct, Enum
    {
        var value = Text(element, label);
        return Enum.GetNames<T>().Contains(value, StringComparer.OrdinalIgnoreCase) && Enum.TryParse<T>(value, true, out var result)
            ? result : throw new InvalidDataException($"{label}无效。");
    }
}
