using System.Globalization;
using System.Text;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Core;

/// <summary>Machine-configured metric output for already compensated, normalized routes.</summary>
public static class NcGenerator
{
    public const int MaxBlockLength = 160;
    public const int MaxMotionBlocks = 500_000;
    public const int MaxDepthPasses = 1_000;
    private const double MaxAxisMagnitude = 1_000_000;
    private const double MaxFeed = 1_000_000;
    // These are the non-cutting feed words used by the supplied LNC programs.
    // The LNC profile currently has no separate UI fields for rapid feeds, so
    // keep the controller-specific defaults together here instead of silently
    // inheriting a modal value from a previous program.
    private const double LncPositioningFeed = 12_000;
    private const double LncRetractFeed = 9_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Generate(ToolpathPlan plan, ConversionConfig? config = null, int programNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(plan);
        config ??= plan.Config;
        config.Validate();
        plan.Config.Validate();
        if (plan.Toolpaths.IsDefaultOrEmpty)
            throw new ArgumentException("没有刀路，不能生成 NC。", nameof(plan));
        if (programNumber is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(programNumber), "程序号应在 1 至 9999 之间。");
        // A plan contains routes and ordering decisions derived from the full
        // configuration.  Reusing it with a different depth, layer policy,
        // origin, machine output, or feed would otherwise produce NC that no
        // longer describes the plan the user reviewed.  Require an exact value
        // match; callers can still choose a different program number.
        if (plan.Config != config)
            throw new ArgumentException("刀路使用了与规划不同的加工参数，请重新生成刀路。", nameof(config));

        var tool = config.Tool;
        var machining = config.Machining;
        var machine = config.Machine;
        var lnc = machine.Profile == MachineProfile.LncRouter;
        var depths = DepthPasses(config);
        long segments = 0;
        foreach (var path in plan.Toolpaths)
        {
            if (path.Points.IsDefaultOrEmpty || path.Points.Length < 4 || path.Points[0] != path.Points[^1])
                throw new ArgumentException($"刀路 {path.ContourId} 不是有效的显式闭环。", nameof(plan));
            if (!Enum.IsDefined(path.Direction) || path.Direction != machining.CutDirection)
                throw new ArgumentException($"刀路 {path.ContourId} 的走刀方向与配置不一致。", nameof(plan));
            segments += path.Points.Length - 1L;
        }
        var motionBlocks = segments * depths.Count;
        if (motionBlocks > MaxMotionBlocks)
            throw new ArgumentException($"刀路需要 {motionBlocks} 个切削运动块，最多允许 {MaxMotionBlocks} 个。", nameof(plan));
        // Preflight the complete output, including ramp loops and quantization, before allocating NC text.
        var prepared = new List<PreparedRoute>(plan.Toolpaths.Length);
        long fullMotionCount = 1 + (machine.ParkAtEnd ? 2 : 0);
        foreach (var path in plan.Toolpaths)
        {
            var route = PrepareRoute(path, config, depths.Count);
            fullMotionCount += route.BlockCount * depths.Count;
            if (fullMotionCount > MaxMotionBlocks)
                throw new ArgumentException($"完整刀路（含斜坡和定位）超过 {MaxMotionBlocks} 个运动块，未生成部分 NC。", nameof(plan));
            prepared.Add(route);
        }

        var result = new StringBuilder();
        void Line(string value)
        {
            if (value.Length > MaxBlockLength)
                throw new ArgumentException($"生成的 NC 行超过 {MaxBlockLength} 字符限制。");
            result.Append(value).Append('\n');
        }
        if (machine.UseProgramEnvelope)
        {
            Line("%");
            Line("O" + programNumber.ToString("D4", Invariant));
        }
        var safeZ = Axis(machining.SafeZ);
        var plungeFeed = Feed(machining.PlungeFeed);
        var cuttingFeed = Feed(machining.CuttingFeed);
        var rampFeed = Feed(machining.RampFeed);
        var lncPositioningFeed = lnc ? Feed(LncPositioningFeed) : "";
        var lncRetractFeed = lnc ? Feed(LncRetractFeed) : "";

        if (lnc)
        {
            // The supplied BaoYuan programs use a deliberately small dialect:
            // one modal setup block, separate T/M/G43 blocks, and no Fanuc
            // envelope or comment syntax in the physical file.  Keeping the
            // annotated path comments here is useful for preview; Write()
            // removes them before sending the file to the controller.
            Line("(CAD2NC PRE-COMPENSATED TOOL-CENTRE ROUTES)");
            Line("(VERIFY PREVIEW, ZERO, DEPTH AND WORKHOLDING BEFORE RUNNING)");
            Line("G54 G49 G90");
            Line($"T{tool.ToolNumber.ToString(Invariant)}{(machine.ToolChange == ToolChangeMode.ToolWithM06 ? " M06" : "")}");
            Line($"{(tool.SpindleDirection == SpindleDirection.Clockwise ? "M03" : "M04")} S{machining.SpindleSpeed.ToString(Invariant)}");
            Line($"G43 H{tool.LengthOffset.ToString(Invariant)}");
        }
        else
        {
            Line("(CAD2NC PRE-COMPENSATED TOOL-CENTRE ROUTES)");
            Line("(VERIFY PREVIEW, ZERO, DEPTH AND WORKHOLDING BEFORE RUNNING)");
            Line("G21 G17 G90 G94");
            Line("G40 G49 G80");
            Line("G54");
            Line("M05");
            Line($"T{tool.ToolNumber.ToString(Invariant)}{(machine.ToolChange == ToolChangeMode.ToolWithM06 ? " M06" : "")}");
            Line($"S{machining.SpindleSpeed.ToString(Invariant)} {(tool.SpindleDirection == SpindleDirection.Clockwise ? "M03" : "M04")}");
            Line($"G00 G43 H{tool.LengthOffset.ToString(Invariant)} Z{safeZ}");
        }
        Line($"(CUT ORDER: {(machining.PrioritizePartStability ? "DEPTH-MAJOR" : "PATH-MAJOR")})");
        Line($"(LEAVE SKIN: {machining.LeaveSkin.ToString("0.###", Invariant)} MM)");

        void EmitOperation(int index, int pass)
        {
            var path = plan.Toolpaths[index];
            var route = prepared[index];
            var start = path.Points[0];
            Line($"(PATH {(index + 1).ToString(Invariant)} PASS {(pass + 1).ToString(Invariant)}/{depths.Count.ToString(Invariant)}: {SafeComment(path.ContourId)} / {SafeComment(path.Layer)})");
            if (lnc)
                Line($"G00 X{Axis(start.X)} Y{Axis(start.Y)} Z{safeZ} F{lncPositioningFeed}");
            else
            {
                Line($"G00 Z{safeZ}");
                Line($"G00 X{Axis(start.X)} Y{Axis(start.Y)}");
            }
            if (machining.EntryMode == EntryMode.Ramp)
            {
                var entryZ = pass == 0 ? config.SurfaceZ : depths[pass - 1];
                Line($"G01 Z{Axis(entryZ)} F{plungeFeed}");
                for (var rampIndex = 0; rampIndex < route.Ramp.Count; rampIndex++)
                {
                    var step = route.Ramp[rampIndex];
                    var stepZ = rampIndex == route.Ramp.Count - 1 ? depths[pass]
                        : entryZ + (depths[pass] - entryZ) * step.Fraction;
                    Line($"G01 X{Axis(step.Point.X)} Y{Axis(step.Point.Y)} Z{Axis(stepZ)}{(lnc || rampIndex == 0 ? " F" + rampFeed : "")}");
                }
            }
            else Line($"G01 Z{Axis(depths[pass])} F{plungeFeed}");
            for (var pointIndex = 0; pointIndex < route.Finish.Count; pointIndex++)
            {
                var point = route.Finish[pointIndex];
                var feed = lnc || pointIndex == 0 ? " F" + cuttingFeed : "";
                Line(lnc
                    ? $"G01 X{Axis(point.X)} Y{Axis(point.Y)} Z{Axis(depths[pass])}{feed}"
                    : $"G01 X{Axis(point.X)} Y{Axis(point.Y)}{feed}");
            }
            Line(lnc ? $"G00 Z{safeZ} F{lncRetractFeed}" : $"G00 Z{safeZ}");
        }

        // A positive leave-skin value deliberately keeps every contour attached
        // to the sheet until the final pass. Route order remains independent
        // from the number of configured depth passes.
        if (machining.PrioritizePartStability)
        {
            for (var pass = 0; pass < depths.Count; pass++)
                for (var index = 0; index < plan.Toolpaths.Length; index++) EmitOperation(index, pass);
        }
        else
        {
            for (var index = 0; index < plan.Toolpaths.Length; index++)
                for (var pass = 0; pass < depths.Count; pass++) EmitOperation(index, pass);
        }
        Line("M05");
        if (machine.ParkAtEnd)
        {
            Line($"G00 Z{Axis(machine.ParkZ)}");
            Line(lnc ? $"X{Axis(machine.ParkX)} Y{Axis(machine.ParkY)}"
                : $"G00 X{Axis(machine.ParkX)} Y{Axis(machine.ParkY)}");
            if (lnc) Line("M05");
        }
        // Keep the active tool length offset through LNC parking and its controller-specific trailer.
        if (machine.Profile == MachineProfile.LegacyFanuc) Line("G49");
        if (machine.EmitM17) Line("M17");
        Line("M30");
        if (machine.UseProgramEnvelope) Line("%");
        return result.ToString();
    }

    /// <summary>Returns the configured stock-aware depth levels followed by the exact final depth.</summary>
    public static IReadOnlyList<double> DepthPasses(ConversionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        var machining = config.Machining;
        if (machining.LeaveSkin > 0)
        {
            var bottomZ = config.SurfaceZ - machining.BoardThickness;
            var skinZ = bottomZ + machining.LeaveSkin;
            // A positive leave-skin value is always an explicit two-pass
            // operation: first leave the configured bottom skin, then make
            // the exact final cut. Configuration validation rejects a final
            // depth at or above this layer, so these levels stay distinct.
            return Array.AsReadOnly(new[] { skinZ, machining.CuttingDepth });
        }
        // Stable ordering is independent from Z-depth layering.  Only an
        // explicitly enabled layered-cutting setting may create intermediate
        // depth passes; disabling it means one move to the requested final Z.
        if (!machining.LayeredCutting)
            return Array.AsReadOnly(new[] { machining.CuttingDepth });

        var distance = Math.Abs(config.SurfaceZ - machining.CuttingDepth);
        var count = Math.Ceiling(distance / machining.MaxStepDown - 1e-12);
        if (!double.IsFinite(count) || count > MaxDepthPasses)
            throw new ArgumentException($"分层数量超过 {MaxDepthPasses} 层上限。", nameof(config));
        var result = new List<double>((int)count);
        var direction = machining.CuttingDepth < config.SurfaceZ ? -1 : 1;
        var fullSteps = (int)Math.Floor(distance / machining.MaxStepDown + 1e-12);
        for (var i = 1; i <= fullSteps; i++)
            result.Add(config.SurfaceZ + direction * machining.MaxStepDown * i);
        if (result.Count == 0 || Math.Abs(result[^1] - machining.CuttingDepth) > Math.Max(1e-12, distance * 1e-12))
            result.Add(machining.CuttingDepth);
        else
            result[^1] = machining.CuttingDepth;
        return result.AsReadOnly();
    }

    public static bool UsesAutomaticSafetyLayering(ConversionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return false;
    }

    private sealed record RampStep(Point2 Point, double Fraction);
    private sealed record PreparedRoute(IReadOnlyList<RampStep> Ramp, IReadOnlyList<Point2> Finish)
    {
        public long BlockCount => 4L + Ramp.Count + Finish.Count;
    }

    private static PreparedRoute PrepareRoute(Toolpath path, ConversionConfig config, int depthCount)
    {
        if (!RouteGeometryHelper.TryQuantizeClosedRoute(path.Points, out var quantized))
            throw new ArgumentException($"刀路 {path.ContourId} 存在重复点、非有限坐标或按 0.001 mm 舍入后退化的线段，未生成部分 NC。");
        var points = quantized.Points;
        var lengths = quantized.SegmentLengths;
        var segmentCount = lengths.Length;
        var ring = new Polygon(new LinearRing(points.Select(p => new Coordinate(p.X, p.Y)).ToArray()));
        if (ring.Area <= 0 || !ring.IsValid)
            throw new ArgumentException($"刀路 {path.ContourId} 按 0.001 mm 舍入后不是有效闭环，未生成部分 NC。");
        if (config.Machining.EntryMode == EntryMode.Vertical)
            return new([], path.Points.Skip(1).ToArray());

        var perimeter = quantized.Perimeter;
        var target = config.Machining.RampLength;
        if (!RouteGeometryHelper.TryAdvanceAlongClosedRoute(path.Points, target,
                out var targetPosition, out var hasEffectiveMovement) || !hasEffectiveMovement)
            throw new ArgumentException($"刀路 {path.ContourId} 的斜坡长度无法可靠计算。");
        // Reject pathological multi-loop ramps before walking them or allocating their points.
        var minimumBlocks = (Math.Floor(target / perimeter) * segmentCount + segmentCount + 4) * depthCount;
        if (!double.IsFinite(minimumBlocks) || minimumBlocks > MaxMotionBlocks)
            throw new ArgumentException($"刀路 {path.ContourId} 的多圈斜坡超过 {MaxMotionBlocks} 个运动块，未生成部分 NC。");
        var ramp = new List<RampStep>();
        var travelled = 0d;
        var edge = 0;
        var end = points[0];
        while (travelled < target)
        {
            if ((ramp.Count + segmentCount + 5L) * depthCount > MaxMotionBlocks)
                throw new ArgumentException($"刀路 {path.ContourId} 的斜坡超过 {MaxMotionBlocks} 个运动块，未生成部分 NC。");
            var advance = Math.Min(lengths[edge], target - travelled);
            var fraction = advance / lengths[edge];
            var a = points[edge]; var b = points[edge + 1];
            var nextTravelled = Math.Min(target, travelled + advance);
            if (nextTravelled <= travelled) throw new ArgumentException($"刀路 {path.ContourId} 的斜坡长度无法可靠计算。");
            var next = nextTravelled >= target
                ? targetPosition.Point
                : fraction >= 1 ? b : RouteGeometryHelper.Quantize(
                    new Point2(a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction));
            travelled = nextTravelled;
            if (next != end) ramp.Add(new(next, travelled / target));
            else if (ramp.Count > 0) ramp[^1] = ramp[^1] with { Fraction = travelled / target };
            end = next;
            if (fraction >= 1 || end == b) edge = (edge + 1) % segmentCount;
        }
        if (ramp.Count == 0) throw new ArgumentException($"刀路 {path.ContourId} 的斜坡按 0.001 mm 舍入后没有有效 XY 移动。");
        ramp[^1] = ramp[^1] with { Fraction = 1 };
        // Finish one entire closed lap from the ramp endpoint, including the entrance portion.
        var finish = new List<Point2>(segmentCount + 1);
        var previous = end;
        for (var index = 0; index < segmentCount; index++)
        {
            var next = points[(edge + index + 1) % segmentCount];
            if (next != previous) { finish.Add(next); previous = next; }
        }
        if (previous != end) finish.Add(end);
        return new(ramp, finish);
    }

    public static string Write(string path, ToolpathPlan plan, ConversionConfig? config = null, int programNumber = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        var target = Path.GetFullPath(path);
        var extension = Path.GetExtension(target);
        if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase) ||
            ResolvedPath(target).Equals(ResolvedPath(plan.Request.Document.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("NC 输出不能覆盖源 CAD 或使用 DWG/DXF 扩展名。", nameof(path));
        var effectiveConfig = config ?? plan.Config;
        var code = Generate(plan, effectiveConfig, programNumber);
        // 宝元 LNC 的现场样例不接受 Fanuc 括号注释，并使用 ASCII/CRLF
        // 记录格式。Generate 保留注释供预览和严格回放使用；仅在实际写盘
        // 时按目标机床整理，避免改变既有 Fanuc 输出及其测试语义。
        code = FormatForMachine(code, effectiveConfig, windowsLineEndings: true);
        return AtomicFile.WriteText(target, code,
            Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
    }

    /// <summary>
    /// Removes preview-only syntax and applies the line ending convention of the
    /// selected controller.  Generated preview text intentionally retains path
    /// comments so playback can label passes; copies and files must not inherit
    /// those comments for the strict LNC dialect.
    /// </summary>
    public static string FormatForMachine(string code, ConversionConfig config, bool windowsLineEndings = true)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(config);
        if (config.Machine.Profile != MachineProfile.LncRouter) return code;
        var lines = code.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("(", StringComparison.Ordinal))
            .Select(line => line.TrimEnd('\r'));
        return string.Join(windowsLineEndings ? "\r\n" : "\n", lines);
    }

    private static void ValidateAxis(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("NC 坐标必须是有限数值。");
        if (Math.Abs(value) > MaxAxisMagnitude) throw new ArgumentException($"NC 坐标幅值不能超过 {MaxAxisMagnitude:g} mm。");
    }

    private static string Axis(double value)
    {
        ValidateAxis(value);
        return (Math.Abs(value) < 0.0005 ? 0 : value).ToString("F3", Invariant);
    }

    private static string Feed(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > MaxFeed)
            throw new ArgumentException($"NC 进给必须是大于零且不超过 {MaxFeed:g} 的有限数值。");
        var formatted = value.ToString("F3", Invariant).TrimEnd('0').TrimEnd('.');
        if (double.Parse(formatted, Invariant) <= 0) throw new ArgumentException("NC 进给按 0.001 mm/min 舍入后为零。");
        return formatted;
    }

    private static string SafeComment(string value)
    {
        // Normalize before escaping, so full-width parentheses cannot terminate a comment.
        var normalized = value.Normalize(NormalizationForm.FormKD);
        var ascii = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            ascii.Append(character switch
            {
                '(' => '[', ')' => ']', '%' => ' ',
                < ' ' or '\u007f' => ' ',
                > '\u007f' => '?',
                _ => character,
            });
        var singleLine = string.Join(' ', ascii.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length > 48 ? singleLine[..48] : singleLine;
    }

    private static string ResolvedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) ? new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full : full;
    }
}
