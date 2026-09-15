using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cad2Nc.Core;

public readonly record struct NcPosition(double X, double Y, double Z, bool IsZKnown = true)
{
    public static NcPosition Lerp(NcPosition start, NcPosition end, double progress) => progress <= 0 ? start : progress >= 1 ? end : new(
        start.X + (end.X - start.X) * progress, start.Y + (end.Y - start.Y) * progress,
        start.Z + (end.Z - start.Z) * progress, start.IsZKnown && end.IsZKnown);
}

public enum NcMotionKind { Rapid, Linear, ArcClockwise, ArcCounterclockwise }

/// <summary>Arc geometry in millimetres. Sweep is positive counterclockwise, negative clockwise.</summary>
public sealed record NcArcGeometry(double CenterX, double CenterY, double Radius, double StartAngle, double SweepAngle);

public sealed record NcMotion(NcPosition Start, NcPosition End, NcMotionKind Kind, double FeedRate,
    int LineIndex, int PathNumber, int PassNumber, int PassCount, string PathLabel,
    double StartSeconds, double DurationSeconds, NcArcGeometry? Arc = null)
{
    public int OperationNumber { get; internal init; }
    public int OperationCount { get; internal init; }
    public double EndSeconds => StartSeconds + DurationSeconds;
    public NcPosition Sample(double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        if (progress <= 0) return Start;
        if (progress >= 1) return End;
        var linear = NcPosition.Lerp(Start, End, progress);
        if (Arc is not { } arc) return linear;
        var angle = arc.StartAngle + arc.SweepAngle * progress;
        return new(arc.CenterX + arc.Radius * Math.Cos(angle), arc.CenterY + arc.Radius * Math.Sin(angle), linear.Z, linear.IsZKnown);
    }

    /// <summary>Display approximation only; playback and timing use the original analytic arc.</summary>
    public int PreviewSegmentCount
    {
        get
        {
            if (Arc is not { } arc) return 1;
            var chordAngle = 2 * Math.Acos(Math.Clamp(1 - .02 / arc.Radius, -1, 1));
            var step = Math.Min(Math.PI / 36, chordAngle);
            return Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.SweepAngle) / step));
        }
    }

    public IEnumerable<NcPosition> GetPreviewPoints()
    {
        var count = PreviewSegmentCount;
        yield return Start;
        for (var index = 1; index < count; index++) yield return Sample((double)index / count);
        yield return End;
    }
}

public sealed record NcPlaybackFrame(NcPosition Position, int MotionIndex, double MotionProgress, NcMotion Motion);

public sealed record NcBounds(double MinX, double MinY, double MaxX, double MaxY, double? MinZ, double? MaxZ)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

/// <summary>Programmed work coordinates; machine offsets and rapid times are not machine simulation.</summary>
public sealed class NcPlaybackProgram
{
    public ImmutableArray<NcMotion> Motions { get; }
    public NcPosition? InitialPosition { get; }
    public string WorkCoordinateSystem { get; }
    public NcBounds? Bounds { get; }
    public double DurationSeconds => Motions.IsEmpty ? 0 : Motions[^1].EndSeconds;

    internal NcPlaybackProgram(ImmutableArray<NcMotion> motions, NcPosition? initialPosition, string workCoordinateSystem = "G54")
    {
        Motions = motions; InitialPosition = initialPosition; WorkCoordinateSystem = workCoordinateSystem;
        var minX = double.PositiveInfinity; var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity;
        double? minZ = null, maxZ = null;
        void Include(NcPosition point)
        {
            minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
            if (point.IsZKnown) { minZ = Math.Min(minZ ?? point.Z, point.Z); maxZ = Math.Max(maxZ ?? point.Z, point.Z); }
        }
        if (initialPosition is { } initial) Include(initial);
        foreach (var motion in motions)
        {
            Include(motion.Start); Include(motion.End);
            if (motion.Arc is not { } arc) continue;
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var angle = quadrant * Math.PI / 2;
                var delta = (arc.SweepAngle > 0 ? angle - arc.StartAngle : arc.StartAngle - angle) % Math.Tau;
                if (delta < 0) delta += Math.Tau;
                if (delta <= Math.Abs(arc.SweepAngle) + 1e-10)
                    Include(new(arc.CenterX + arc.Radius * Math.Cos(angle), arc.CenterY + arc.Radius * Math.Sin(angle), 0, false));
            }
        }
        if (double.IsFinite(minX)) Bounds = new(minX, minY, maxX, maxY, minZ, maxZ);
    }

    public NcPlaybackFrame? Sample(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (Motions.IsEmpty) return null;
        seconds = Math.Clamp(seconds, 0, DurationSeconds);
        var low = 0; var high = Motions.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (Motions[middle].EndSeconds <= seconds) low = middle + 1;
            else high = middle;
        }
        var index = Math.Min(low, Motions.Length - 1);
        var motion = Motions[index];
        var progress = motion.DurationSeconds <= 0 || seconds >= motion.EndSeconds ? 1 : Math.Clamp((seconds - motion.StartSeconds) / motion.DurationSeconds, 0, 1);
        return new(motion.Sample(progress), index, progress, motion);
    }
}

/// <summary>Reads a bounded, explicit subset of metric/imperial G-code in programmed work coordinates.</summary>
public static partial class NcPlaybackParser
{
    public const double RapidFeedRate = 10_000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^PATH\s+(\d+)\s+PASS\s+(\d+)/(\d+)(?:\s*:\s*(.*))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PathCommentPattern();

    // Generated programs retain the stricter requirement to establish XYZ before cutting.
    public static NcPlaybackProgram Parse(string code) => ParseCore(code, false, CancellationToken.None, null);

    /// <summary>Strict generated-program parsing with only the configured terminal controller trailer enabled.</summary>
    public static NcPlaybackProgram ParseGenerated(string code, ConversionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        return ParseCore(code, false, CancellationToken.None, null, config.Machine.EmitM17);
    }

    internal static NcPlaybackProgram ParseExternal(string code, CancellationToken token, List<NcNotice> notices)
        => ParseCore(code, true, token, notices);

    private static NcPlaybackProgram ParseCore(string code, bool external, CancellationToken token, List<NcNotice>? notices,
        bool generatedM17 = false)
    {
        ArgumentNullException.ThrowIfNull(code);
        var motions = ImmutableArray.CreateBuilder<NcMotion>();
        double? x = null, y = null, z = null, feed = null;
        NcPosition? initial = null;
        NcMotionKind? mode = null;
        var unitScale = 1d; var absolute = true; var centerAbsolute = false;
        var workSystem = 54; var elapsed = 0d; var previewPoints = 0L;
        var pathNumber = 0; var passNumber = 0; var passCount = 0; var pathLabel = ""; var operationNumber = 0;
        var ended = false; var anchorNoted = false; var unknownZNoted = false;
        var hasLocated = false;
        int? pendingTrailerLine = null;
        var specified = new HashSet<int>(); var defaultsUsed = new HashSet<int>();
        using var reader = new StringReader(code);
        string? raw; var lineIndex = -1;
        while ((raw = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            lineIndex++;
            if (lineIndex >= (external ? NcDocumentReader.MaxLines : 3_000_000)) throw Error(lineIndex, "程序超过播放行数上限。");
            var block = StripComments(raw, lineIndex, comment =>
            {
                var match = PathCommentPattern().Match(comment);
                if (!match.Success) return;
                if (!int.TryParse(match.Groups[1].Value, Invariant, out pathNumber) || pathNumber < 1 ||
                    !int.TryParse(match.Groups[2].Value, Invariant, out passNumber) || passNumber < 1 ||
                    !int.TryParse(match.Groups[3].Value, Invariant, out passCount) || passCount < passNumber)
                    throw Error(lineIndex, "轮廓或分层编号无效。");
                // The colon and label were optional in early generated files;
                // keep accepting those markers while counting their appearance.
                pathLabel = match.Groups[4].Value;
                operationNumber++;
            });
            if (block.Length == 0 || block == "%") continue;
            if (ended) throw Error(lineIndex, "程序结束后仍有程序指令。");
            var words = ReadWords(block, lineIndex);
            if (pendingTrailerLine is { } trailerLine)
            {
                if (!IsStandaloneMBlock(words, 30) && !(external && IsStandaloneMBlock(words, 2)))
                    throw Error(trailerLine, "仅支持程序末尾独立的 M17，之后必须直接以 M2/M30 结束；不能预览中途的控制器专用动作。");
                pendingTrailerLine = null;
            }
            var values = new Dictionary<char, double>();
            var groups = new HashSet<int>();
            var hasEnd = false;
            var hasArcWord = false;
            var hasReferenceReturn = false;
            var hasTrailer = false;
            // Modal changes apply to the entire block, regardless of word order.
            foreach (var (letter, value) in words)
            {
                if (letter == 'G')
                {
                    // G28 is nonmodal. Only the external reader may omit a bounded
                    // reference return; it must not replace the active G0/G1/G2/G3.
                    var group = external && value == 28 ? 9 : GGroup(value);
                    if (group == 0) throw Error(lineIndex, $"不支持 G{Number(value)}，不能可靠预览此程序。");
                    if (!groups.Add(group)) throw Error(lineIndex, "同一行包含同组的多个 G 指令。");
                    specified.Add(group);
                    switch (value)
                    {
                        case 0: mode = NcMotionKind.Rapid; break;
                        case 1: mode = NcMotionKind.Linear; break;
                        case 2: mode = NcMotionKind.ArcClockwise; break;
                        case 3: mode = NcMotionKind.ArcCounterclockwise; break;
                        case 80: mode = null; break;
                        case 20: unitScale = 25.4; break;
                        case 21: unitScale = 1; break;
                        case 28: hasReferenceReturn = true; break;
                        case 90: absolute = true; break;
                        case 91: absolute = false; break;
                        case 90.1: centerAbsolute = true; break;
                        case 91.1: centerAbsolute = false; break;
                        case >= 54 and <= 59:
                            if (workSystem != (int)value && hasLocated)
                                throw Error(lineIndex, "程序定位后切换工件坐标系需要机床偏置数据，暂不支持预览。");
                            workSystem = (int)value;
                            break;
                    }
                }
                else if (letter == 'M')
                {
                    if (value == 17 && (external || generatedM17)) hasTrailer = true;
                    else if (value is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 30)) throw Error(lineIndex, $"不支持 M{Number(value)}，不能可靠预览此程序。");
                    hasEnd |= value is 2 or 30;
                }
                else
                {
                    if (!values.TryAdd(letter, value)) throw Error(lineIndex, $"同一行重复设置 {letter}。");
                    switch (letter)
                    {
                        case 'X': case 'Y': case 'Z': case 'F': break;
                        case 'I': case 'J': case 'R': hasArcWord = true; break;
                        case 'N': case 'O': case 'T': case 'H':
                            if (value < 0 || value > int.MaxValue || value != Math.Truncate(value)) throw Error(lineIndex, $"{letter} 编号无效。");
                            break;
                        case 'S': if (value < 0) throw Error(lineIndex, "主轴转速无效。"); break;
                        default: throw Error(lineIndex, $"不支持的 NC 字段 {letter}。");
                    }
                }
            }
            if (hasTrailer)
            {
                if (!IsStandaloneMBlock(words, 17))
                    throw Error(lineIndex, "M17 仅可作为程序末尾的独立指令，不能与运动或其它指令混用。");
                pendingTrailerLine = lineIndex;
                notices?.Add(new("末尾 M17 的功能由控制器定义，仅保留原文，不模拟其动作；预览仅包含此前显式编程轨迹，该指令的动作和时间未计入。", lineIndex));
                continue;
            }
            ended = hasEnd;
            if (values.TryGetValue('F', out var rawFeed))
            {
                foreach (var group in new[] { 5, 6 }) if (!specified.Contains(group)) defaultsUsed.Add(group);
                var converted = rawFeed * unitScale;
                if (!double.IsFinite(converted) || converted <= 0 || converted > 1_000_000)
                    throw Error(lineIndex, "进给必须是大于零且不超过 1000000 mm/min 的有限数值。");
                feed = converted;
            }
            var hasAxis = values.ContainsKey('X') || values.ContainsKey('Y') || values.ContainsKey('Z');
            if (hasReferenceReturn)
            {
                if (absolute || !hasAxis || hasArcWord || groups.Contains(1) ||
                    values.Any(value => value.Key is 'X' or 'Y' or 'Z' && value.Value != 0))
                    throw Error(lineIndex, "G28 仅支持 G91 模式下显式指定回零轴且位移全为 0 的形式（如 G91 G28 Z0），不能与其它运动混用。");
                // Machine reference coordinates cannot be recovered from work coordinates.
                // Do not invent a move to work Z0, retain stale Z, or join paths across
                // an unknown XY return. Subsequent absolute axes can re-establish position.
                var axes = new List<string>();
                if (values.ContainsKey('X')) { x = null; axes.Add("X"); }
                if (values.ContainsKey('Y')) { y = null; axes.Add("Y"); }
                if (values.ContainsKey('Z')) { z = null; axes.Add("Z"); }
                notices!.Add(new($"G28 机床回参考点的运动未绘制、时间未计入；回零轴 {string.Join("/", axes)} 的工件坐标未知，需后续绝对坐标重新定位，未知起点的移动不绘制。", lineIndex));
                continue;
            }
            if (!hasAxis && !hasArcWord) continue;
            if (mode is null) throw Error(lineIndex, "坐标未指定 G00、G01、G02 或 G03 运动方式。");
            foreach (var group in new[] { 2, 3, 5, 6, 12 }) if (!specified.Contains(group)) defaultsUsed.Add(group);
            var isArc = mode is NcMotionKind.ArcClockwise or NcMotionKind.ArcCounterclockwise;
            if (isArc && !specified.Contains(4)) defaultsUsed.Add(4);
            if (!isArc && hasArcWord) throw Error(lineIndex, "I、J、R 只能用于 G17 平面的圆弧运动。");
            var start = Position(x, y, z, external);
            if (mode != NcMotionKind.Rapid && feed is null) throw Error(lineIndex, "切削运动前必须设置有效进给 F。");
            if ((isArc || (!external && mode != NcMotionKind.Rapid)) && start is null)
                throw Error(lineIndex, "切削运动前必须建立完整起始位置和有效进给。");
            var nextX = ResolveAxis('X', x, values, unitScale, absolute, lineIndex);
            var nextY = ResolveAxis('Y', y, values, unitScale, absolute, lineIndex);
            var nextZ = ResolveAxis('Z', z, values, unitScale, absolute, lineIndex);
            x = nextX; y = nextY; z = nextZ;
            hasLocated |= x is not null || y is not null || z is not null;
            var end = Position(x, y, z, external);
            if (end is null) continue;
            if (start is null)
            {
                initial ??= end;
                if (external && !anchorNoted)
                { notices!.Add(new("起始位置未知：以首个完整的绝对 XY 位置作为预览起点，之前的移动不绘制。", lineIndex)); anchorNoted = true; }
                continue;
            }
            if (external && (!start.Value.IsZKnown || !end.Value.IsZKnown) && !unknownZNoted)
            { notices!.Add(new("部分区段未给出 Z：显示已知的 XY 路线，区段时间只计算已知轴的运动。", lineIndex)); unknownZNoted = true; }
            if (motions.Count >= (external ? NcDocumentReader.MaxMotions : 2_000_000)) throw Error(lineIndex, "程序超过播放运动数量上限。");
            var arc = isArc ? MakeArc(start.Value, end.Value, values, unitScale, centerAbsolute, mode == NcMotionKind.ArcClockwise, lineIndex) : null;
            var xyDistance = arc is null ? double.Hypot(end.Value.X - start.Value.X, end.Value.Y - start.Value.Y) : arc.Radius * Math.Abs(arc.SweepAngle);
            var zDistance = start.Value.IsZKnown && end.Value.IsZKnown ? end.Value.Z - start.Value.Z : 0;
            var distance = double.Hypot(xyDistance, zDistance);
            var effectiveFeed = mode == NcMotionKind.Rapid ? RapidFeedRate : feed!.Value;
            var duration = distance / effectiveFeed * 60;
            if (!double.IsFinite(duration) || !double.IsFinite(elapsed + duration)) throw Error(lineIndex, "运动时间超过可表示范围。");
            var motion = new NcMotion(start.Value, end.Value, mode.Value, effectiveFeed, lineIndex, pathNumber, passNumber, passCount, pathLabel, elapsed, duration, arc)
            {
                OperationNumber = operationNumber,
            };
            previewPoints += motion.PreviewSegmentCount + 1L;
            if (external && previewPoints > NcDocumentReader.MaxPreviewPoints) throw Error(lineIndex, "轨迹超过 200 万显示点，保留源码与参数查看。");
            motions.Add(motion); elapsed += duration;
        }
        if (pendingTrailerLine is { } unfinishedTrailer)
            throw Error(unfinishedTrailer, "末尾 M17 后缺少独立的 M2/M30 程序结束指令。");
        if (operationNumber > 0)
            for (var index = 0; index < motions.Count; index++)
                // A motion before the first PATH/PASS marker has no operation
                // metadata. Keep both fields at zero instead of assigning the
                // later total count to an unlabelled header/positioning move.
                if (motions[index].OperationNumber > 0)
                    motions[index] = motions[index] with { OperationCount = operationNumber };
        if (external)
        {
            var defaults = defaultsUsed.Order().Select(group => group switch { 2 => "G17 XY 平面", 3 => "G90 绝对终点", 4 => "G91.1 增量圆心", 5 => "G94 每分钟进给", 6 => "G21 毫米", _ => "G54 工件坐标系" });
            if (defaultsUsed.Count > 0) notices!.Insert(0, new("未显式指定的初始模式按以下默认值预览：" + string.Join("、", defaults) + "。"));
        }
        return new(motions.ToImmutable(), initial, "G" + workSystem);
    }

    private static bool IsStandaloneMBlock(List<(char Letter, double Value)> words, double code)
        => words.Count(word => word.Letter == 'M' && word.Value == code) == 1 &&
           words.All(word => word.Letter == 'N' || word.Letter == 'M' && word.Value == code);

    private static NcPosition? Position(double? x, double? y, double? z, bool allowUnknownZ)
        => x is { } px && y is { } py && (z is not null || allowUnknownZ) ? new NcPosition(px, py, z ?? 0, z is not null) : null;

    private static double? ResolveAxis(char axis, double? current, Dictionary<char, double> values, double scale, bool absolute, int line)
    {
        if (!values.TryGetValue(axis, out var value)) return current;
        if (!absolute && current is null) throw Error(line, $"{axis} 起始位置未知，不能解释相对坐标。");
        return Axis(value * scale + (absolute ? 0 : current!.Value), line);
    }

    private static NcArcGeometry MakeArc(NcPosition start, NcPosition end, Dictionary<char, double> values, double scale, bool absoluteCenter, bool clockwise, int line)
    {
        var hasI = values.TryGetValue('I', out var i); var hasJ = values.TryGetValue('J', out var j);
        var hasR = values.TryGetValue('R', out var r);
        if (hasR && (hasI || hasJ)) throw Error(line, "圆弧不能同时使用 R 与 I/J。");
        double cx, cy, radius, sweep;
        if (hasR)
        {
            r *= scale; radius = Math.Abs(r);
            var dx = end.X - start.X; var dy = end.Y - start.Y; var chord = double.Hypot(dx, dy);
            if (radius <= 0 || chord <= 1e-10) throw Error(line, "R 圆弧需要非零半径和不同的起终点；整圆请使用 I/J。");
            if (chord > 2 * radius + 1e-8) throw Error(line, "R 圆弧半径小于起终点距离的一半。");
            var height = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
            cx = (start.X + end.X) / 2 - dy / chord * height;
            cy = (start.Y + end.Y) / 2 + dx / chord * height;
            sweep = Sweep(start, end, cx, cy, clockwise, false);
            if ((r > 0 && Math.Abs(sweep) > Math.PI + 1e-10) || (r < 0 && Math.Abs(sweep) < Math.PI - 1e-10))
            { cx = (start.X + end.X) / 2 + dy / chord * height; cy = (start.Y + end.Y) / 2 - dx / chord * height; sweep = Sweep(start, end, cx, cy, clockwise, false); }
        }
        else
        {
            if (!hasI && !hasJ) throw Error(line, "圆弧必须给出 I/J 或 R。");
            if (absoluteCenter && (!hasI || !hasJ)) throw Error(line, "G90.1 绝对圆心必须同时指定 I 和 J。");
            cx = absoluteCenter ? i * scale : start.X + i * scale;
            cy = absoluteCenter ? j * scale : start.Y + j * scale;
            radius = double.Hypot(start.X - cx, start.Y - cy);
            var endRadius = double.Hypot(end.X - cx, end.Y - cy);
            if (radius <= 1e-10 || Math.Abs(radius - endRadius) > Math.Max(.01, radius * 1e-6)) throw Error(line, "I/J 圆弧起终点半径不一致或半径为零。");
            sweep = Sweep(start, end, cx, cy, clockwise, start.X == end.X && start.Y == end.Y);
        }
        Axis(cx, line); Axis(cy, line);
        if (!double.IsFinite(radius) || radius > 1_000_000) throw Error(line, "圆弧半径超过预览范围。");
        return new(cx, cy, radius, Math.Atan2(start.Y - cy, start.X - cx), sweep);
    }

    private static double Sweep(NcPosition start, NcPosition end, double cx, double cy, bool clockwise, bool fullCircle)
    {
        if (fullCircle) return clockwise ? -Math.Tau : Math.Tau;
        var sweep = Math.Atan2(end.Y - cy, end.X - cx) - Math.Atan2(start.Y - cy, start.X - cx);
        if (clockwise) { while (sweep >= 0) sweep -= Math.Tau; }
        else { while (sweep <= 0) sweep += Math.Tau; }
        return sweep;
    }

    internal static int GGroup(double value) => value switch
    {
        0 or 1 or 2 or 3 or 80 => 1, 17 => 2, 90 or 91 => 3, 90.1 or 91.1 => 4,
        94 => 5, 20 or 21 => 6, 40 => 7, 43 or 49 => 8,
        54 or 55 or 56 or 57 or 58 or 59 => 12, _ => 0
    };

    internal static string StripComments(string raw, int lineIndex, Action<string>? onComment = null)
    {
        var result = new StringBuilder(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] == ';') break;
            if (raw[index] == ')') throw Error(lineIndex, "注释括号不匹配。");
            if (raw[index] != '(') { result.Append(raw[index]); continue; }
            var close = raw.IndexOf(')', index + 1);
            if (close < 0 || raw.AsSpan(index + 1, close - index - 1).Contains('(')) throw Error(lineIndex, "注释括号不匹配。");
            onComment?.Invoke(raw[(index + 1)..close].Trim()); result.Append(' '); index = close;
        }
        return result.ToString().Trim();
    }

    internal static List<(char Letter, double Value)> ReadWords(string block, int lineIndex)
    {
        var result = new List<(char, double)>(); var index = 0;
        while (index < block.Length)
        {
            if (result.Count >= 256) throw Error(lineIndex, "单行 NC 字段超过 256 个，暂不能解析。");
            if (char.IsWhiteSpace(block[index])) { index++; continue; }
            var letter = char.ToUpperInvariant(block[index++]);
            if (letter is < 'A' or > 'Z') throw Error(lineIndex, "无法识别 NC 指令。");
            while (index < block.Length && char.IsWhiteSpace(block[index])) index++;
            var start = index;
            if (index < block.Length && block[index] is '+' or '-') index++;
            while (index < block.Length && (char.IsAsciiDigit(block[index]) || block[index] == '.')) index++;
            if (!double.TryParse(block.AsSpan(start, index - start), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out var value) || !double.IsFinite(value))
                throw Error(lineIndex, $"{letter} 数值无效。");
            result.Add((letter, value));
        }
        return result;
    }

    internal static string Number(double value) => value.ToString("0.###############", Invariant);
    private static double Axis(double value, int lineIndex) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000 ? value : throw Error(lineIndex, "坐标幅值超过 1000000 mm。");
    internal static InvalidDataException Error(int lineIndex, string message) => new($"NC 第 {lineIndex + 1} 行：{message}");
}
