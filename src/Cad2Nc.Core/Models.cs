using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Core;

public enum CutSide { Outside, Inside }
public enum CutDirection { Counterclockwise, Clockwise }
public enum SpindleDirection { Clockwise, Counterclockwise }
public enum DiagnosticSeverity { Information, Warning, Error }
public enum MachineProfile { LegacyFanuc, LncRouter }
public enum ZReference { WorkpieceTop, WorkpieceBottom }
public enum OriginMode { ToolpathMinimum, SheetLowerLeft, Manual }
public enum EntryMode { Vertical, Ramp }
public enum ToolChangeMode { ToolWithM06, ToolOnly }

public sealed record Diagnostic(string Code, string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Warning, string? ContourId = null,
    bool RequiresConfirmation = false, int Count = 1, string? RelatedField = null,
    ImmutableArray<string> RelatedContourIds = default, string? Remedy = null)
{
    public ImmutableArray<string> ContourIds => RelatedContourIds.IsDefaultOrEmpty
        ? ContourId is null ? [] : [ContourId]
        : RelatedContourIds;
}

public readonly record struct Point2(double X, double Y);
public readonly record struct Bounds2(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public static Bounds2 FromPoints(IEnumerable<Point2> points)
    {
        var values = points.ToArray();
        return values.Length == 0 ? default : new(values.Min(p => p.X), values.Min(p => p.Y), values.Max(p => p.X), values.Max(p => p.Y));
    }
}

public sealed record ToolConfig
{
    public int ToolNumber { get; init; } = 2;
    public int LengthOffset { get; init; } = 2;
    public string ToolType { get; init; } = "6 mm 直刀";
    public double Diameter { get; init; } = 6;
    public SpindleDirection SpindleDirection { get; init; } = SpindleDirection.Clockwise;
    [JsonIgnore] public double Radius => Diameter / 2;
}

public sealed record MachiningConfig
{
    public int SpindleSpeed { get; init; } = 18000;
    public double CuttingFeed { get; init; } = 10000;
    public double CuttingDepth { get; init; } = -0.10;
    public CutDirection CutDirection { get; init; } = CutDirection.Counterclockwise;
    public double EdgeAllowance { get; init; } = 1;
    public double SafeZ { get; init; } = 5;
    public double PlungeFeed { get; init; } = 1000;
    public double MaxStepDown { get; init; } = 3;
    public double BoardThickness { get; init; } = 17;
    public ZReference ZReference { get; init; } = ZReference.WorkpieceTop;
    public bool LayeredCutting { get; init; } = true;
    public bool PrioritizePartStability { get; init; } = true;
    public double LeaveSkin { get; init; }
    public EntryMode EntryMode { get; init; } = EntryMode.Vertical;
    public double RampLength { get; init; } = 80;
    public double RampFeed { get; init; } = 3000;
}

public sealed record MachineConfig
{
    public MachineProfile Profile { get; init; } = MachineProfile.LegacyFanuc;
    public ToolChangeMode ToolChange { get; init; } = ToolChangeMode.ToolWithM06;
    public bool UseProgramEnvelope { get; init; } = true;
    public bool ParkAtEnd { get; init; }
    public double ParkX { get; init; } = 200;
    public double ParkY { get; init; } = 2600;
    public double ParkZ { get; init; } = 50;
    public bool EmitM17 { get; init; }
}

/// <summary>Factories are explicit: record defaults retain version-one JSON semantics.</summary>
public static class ConfigurationDefaults
{
    public static ConversionConfig Legacy() => new();
    public static ConversionConfig Lnc() => new()
    {
        OriginMode = OriginMode.SheetLowerLeft,
        Machine = new() { Profile = MachineProfile.LncRouter, ToolChange = ToolChangeMode.ToolOnly,
            UseProgramEnvelope = false, ParkAtEnd = true, EmitM17 = true },
        Machining = new() { ZReference = ZReference.WorkpieceBottom, SafeZ = 45,
            PlungeFeed = 8000, LayeredCutting = false, EntryMode = EntryMode.Ramp }
    };
    public static ConversionConfig ForProfile(MachineProfile profile) => profile switch
    {
        MachineProfile.LegacyFanuc => Legacy(), MachineProfile.LncRouter => Lnc(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
    public static string Name(MachineProfile profile) => profile switch
    {
        MachineProfile.LncRouter => "宝元 LNC · 当前木工开料机",
        MachineProfile.LegacyFanuc => "通用 Fanuc · 旧版",
        _ => "未知机床"
    };
}

public sealed record ConversionConfig
{
    public ToolConfig Tool { get; init; } = new();
    public MachiningConfig Machining { get; init; } = new();
    public MachineConfig Machine { get; init; } = new();
    public OriginMode OriginMode { get; init; } = OriginMode.ToolpathMinimum;
    [JsonIgnore] public double NetToolOffset => Tool.Radius - Machining.EdgeAllowance;
    [JsonIgnore] public double SurfaceZ => Machining.ZReference == ZReference.WorkpieceBottom ? Machining.BoardThickness : 0;
    public IReadOnlyDictionary<string, string> GetValidationErrors()
    {
        var errors = new Dictionary<string, string>();
        void Range(string field, double value, double min, double max) { if (!double.IsFinite(value) || value < min || value > max) errors[field] = $"数值应在 {min:g} 至 {max:g} 之间"; }
        void Resolution(string field, double value) { if (double.IsFinite(value) && Math.Abs(value * 1000 - Math.Round(value * 1000)) > 1e-8) errors[field] = "精度最多为 0.001 mm"; }
        if (Tool is null || Machining is null || Machine is null) { errors["Config"] = "参数不完整"; return errors; }
        Range(nameof(Tool.ToolNumber), Tool.ToolNumber, 1, 9999);
        Range(nameof(Tool.LengthOffset), Tool.LengthOffset, 1, 9999);
        Range(nameof(Tool.Diameter), Tool.Diameter, double.Epsilon, 10000);
        if (string.IsNullOrWhiteSpace(Tool.ToolType)) errors[nameof(Tool.ToolType)] = "请输入刀具名称";
        if (!Enum.IsDefined(Tool.SpindleDirection)) errors[nameof(Tool.SpindleDirection)] = "主轴方向无效";
        if (!Enum.IsDefined(Machining.CutDirection)) errors[nameof(Machining.CutDirection)] = "走刀方向无效";
        Range(nameof(Machining.SpindleSpeed), Machining.SpindleSpeed, 1, 999999);
        Range(nameof(Machining.CuttingFeed), Machining.CuttingFeed, 0.001, 1000000);
        Range(nameof(Machining.CuttingDepth), Machining.CuttingDepth, -1000000, 1000000);
        Range(nameof(Machining.EdgeAllowance), Machining.EdgeAllowance, 0, 10000);
        Range(nameof(Machining.SafeZ), Machining.SafeZ, 0.1, 1000000);
        Range(nameof(Machining.PlungeFeed), Machining.PlungeFeed, 0.001, 1000000);
        Range(nameof(Machining.MaxStepDown), Machining.MaxStepDown, 0.001, 1000000);
        Range(nameof(Machining.LeaveSkin), Machining.LeaveSkin, 0, 1000000);
        if (double.IsFinite(Machining.LeaveSkin) && Machining.LeaveSkin < 0)
            errors[nameof(Machining.LeaveSkin)] = "留皮厚度不能小于 0";
        Range(nameof(Machining.BoardThickness), Machining.BoardThickness, 0.001, 1000000);
        Range(nameof(Machining.RampLength), Machining.RampLength, 0.001, 1000000);
        Range(nameof(Machining.RampFeed), Machining.RampFeed, 0.001, 1000000);
        Range(nameof(Machine.ParkX), Machine.ParkX, -1000000, 1000000);
        Range(nameof(Machine.ParkY), Machine.ParkY, -1000000, 1000000);
        Range(nameof(Machine.ParkZ), Machine.ParkZ, -1000000, 1000000);
        if (!Enum.IsDefined(Machine.Profile)) errors[nameof(Machine.Profile)] = "机床配置无效";
        if (!Enum.IsDefined(Machine.ToolChange)) errors[nameof(Machine.ToolChange)] = "刀具调用方式无效";
        if (!Enum.IsDefined(OriginMode)) errors[nameof(OriginMode)] = "XY 原点方式无效";
        if (!Enum.IsDefined(Machining.ZReference)) errors[nameof(Machining.ZReference)] = "Z 零点基准无效";
        if (!Enum.IsDefined(Machining.EntryMode)) errors[nameof(Machining.EntryMode)] = "下刀方式无效";
        if (Machining.CuttingDepth >= SurfaceZ) errors[nameof(Machining.CuttingDepth)] = "最终 Z 必须低于板材上表面";
        if (Machining.SafeZ <= SurfaceZ) errors[nameof(Machining.SafeZ)] = "安全 Z 必须高于板材上表面";
        if (Machine.ParkAtEnd && Machine.ParkZ < Machining.SafeZ) errors[nameof(Machine.ParkZ)] = "停车 Z 不能低于安全 Z";
        Resolution(nameof(Machining.CuttingDepth), Machining.CuttingDepth);
        Resolution(nameof(Machining.SafeZ), Machining.SafeZ);
        Resolution(nameof(Machining.BoardThickness), Machining.BoardThickness);
        Resolution(nameof(Machining.RampLength), Machining.RampLength);
        Resolution(nameof(Machine.ParkX), Machine.ParkX);
        Resolution(nameof(Machine.ParkY), Machine.ParkY);
        Resolution(nameof(Machine.ParkZ), Machine.ParkZ);
        if (Machining.LeaveSkin > 0)
        {
            if (Machining.LeaveSkin >= Machining.BoardThickness)
                errors[nameof(Machining.LeaveSkin)] = "留皮厚度必须小于板厚";
            else if (Machining.CuttingDepth >= SurfaceZ - Machining.BoardThickness + Machining.LeaveSkin)
                errors[nameof(Machining.LeaveSkin)] = "最终 Z 必须低于留皮层，才能生成第二遍切割";
        }
        return errors;
    }
    public void Validate()
    {
        var errors = GetValidationErrors();
        if (errors.Count != 0) throw new ArgumentException(string.Join("；", errors.Select(e => $"{e.Key}: {e.Value}")));
    }
}

/// <summary>The polygon is defensively copied at the API boundary.</summary>
public sealed class Contour
{
    private readonly Polygon geometry;
    public string Id { get; }
    public string Layer { get; }
    public bool IsOuterCandidate { get; }
    public ImmutableArray<string> SourceEntityTypes { get; }
    public Polygon Geometry => (Polygon)geometry.Copy();
    public ImmutableArray<Point2> Points { get; }
    public Bounds2 Bounds { get; }
    public double Area => geometry.Area;
    public string Fingerprint { get; }
    public Contour(string id, string layer, Polygon geometry, bool isOuterCandidate = false, IEnumerable<string>? sourceEntityTypes = null)
    {
        if (string.IsNullOrWhiteSpace(id) || geometry.IsEmpty || geometry.Area <= 0 || !geometry.IsValid)
            throw new ArgumentException("轮廓必须是有效的非空闭环");
        Id = id; Layer = layer; this.geometry = (Polygon)geometry.Copy(); IsOuterCandidate = isOuterCandidate;
        SourceEntityTypes = (sourceEntityTypes ?? []).ToImmutableArray();
        Points = geometry.ExteriorRing.Coordinates.Select(c => new Point2(c.X, c.Y)).ToImmutableArray();
        Bounds = Bounds2.FromPoints(Points);
        var normalized = (Polygon)geometry.Copy(); normalized.Normalize();
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(layer).Concat(normalized.AsBinary()).ToArray()));
    }
}

public sealed record CadDocument(string SourcePath, string SourceHash, ImmutableArray<Contour> Contours,
    ImmutableArray<Diagnostic> Diagnostics, bool UnitsUnspecified = false, string Units = "mm")
{
    public string Id => SourceHash;
    public bool RequiresDiagnosticsConfirmation => Diagnostics.Any(d => d.RequiresConfirmation);
    public ImmutableDictionary<string, ContourSelection> DefaultSelection => Contours.ToImmutableDictionary(c => c.Id, c => new ContourSelection(!c.IsOuterCandidate, CutSide.Outside));
}

public sealed record CadImportResult(CadDocument Document)
{
    public ImmutableArray<Diagnostic> Diagnostics => Document.Diagnostics;
    public bool RequiresUnitsConfirmation => Document.UnitsUnspecified;
    public bool RequiresDiagnosticsConfirmation => Document.RequiresDiagnosticsConfirmation;
}

public sealed record ContourSelection(bool Included = true, CutSide Side = CutSide.Outside);
public sealed record WorkpieceSetup
{
    public string? SheetContourId { get; init; }
    public Point2? ManualOrigin { get; init; }
}
public sealed record PlanningRequest(CadDocument Document, ConversionConfig Config,
    ImmutableDictionary<string, ContourSelection> Selection, long Revision = 0, WorkpieceSetup? Workpiece = null)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Document.SourceHash + JsonSerializer.Serialize(Config) + JsonSerializer.Serialize(Workpiece ?? new()) + JsonSerializer.Serialize(Selection.OrderBy(p => p.Key)))));
}

public sealed record Toolpath(string ContourId, string Layer, ImmutableArray<Point2> Points,
    double OffsetDistance, CutDirection Direction)
{
    public Bounds2 Bounds => Bounds2.FromPoints(Points);
    public double Length => Points.Zip(Points.Skip(1), (a, b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2))).Sum();
}

public sealed record FinishedProfile(string ContourId, ImmutableArray<Point2> Points);
public sealed record ToolpathPlan(PlanningRequest Request, ImmutableArray<Toolpath> Toolpaths,
    ImmutableArray<FinishedProfile> FinishedProfiles, Point2 Origin, ImmutableArray<Diagnostic> Diagnostics,
    ImmutableDictionary<string, double> Stats)
{
    public long Revision => Request.Revision;
    public string Fingerprint => Request.Fingerprint;
    public ConversionConfig Config => Request.Config;
    public Bounds2 Bounds => Bounds2.FromPoints(Toolpaths.SelectMany(p => p.Points));
}

public interface ICadImporter
{
    Task<CadImportResult> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class CadImportException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class ToolpathGenerationException(string message, IEnumerable<Diagnostic>? diagnostics = null) : Exception(message)
{
    public ImmutableArray<Diagnostic> Diagnostics { get; } = (diagnostics ?? []).ToImmutableArray();
}
