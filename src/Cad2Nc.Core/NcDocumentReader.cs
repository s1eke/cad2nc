using System.Text;

namespace Cad2Nc.Core;

public sealed record NcParameter(string Name, string Value, int LineIndex);
public sealed record NcNotice(string Message, int? LineIndex = null);

/// <summary>A read-only source document. Explicitly bounded machine actions may be omitted with notices;
/// unsupported geometry or control flow never produces a partial playback program.</summary>
public sealed record NcDocument(string SourcePath, string Code, IReadOnlyList<string> Lines,
    IReadOnlyList<NcParameter> Parameters, IReadOnlyList<NcNotice> Notices,
    NcPlaybackProgram? Program, string? PlaybackError);

public static class NcDocumentReader
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public const int MaxLines = 1_000_000;
    public const int MaxMotions = 1_000_000;
    public const int MaxPreviewPoints = 2_000_000;
    private const int MaxParameterValues = 10_000;

    public static async Task<NcDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sourcePath = Path.GetFullPath(path);
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) throw new InvalidDataException("NC 文件超过 32 MiB，暂不能打开。");
        using var memory = new MemoryStream((int)stream.Length);
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > MaxBytes) throw new InvalidDataException("NC 文件超过 32 MiB，暂不能打开。");
            memory.Write(buffer, 0, count);
        }
        var bytes = memory.ToArray();
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (code, encodingNotice) = Decode(bytes);
            var notices = new List<NcNotice>();
            if (encodingNotice is not null) notices.Add(new(encodingNotice));
            return ParseCore(code, sourcePath, cancellationToken, notices);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static NcDocument Parse(string code, string sourcePath = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (Encoding.UTF8.GetByteCount(code) > MaxBytes) throw new InvalidDataException("NC 文本超过 32 MiB，暂不能打开。");
        return ParseCore(code, sourcePath, cancellationToken, []);
    }

    private static NcDocument ParseCore(string code, string sourcePath, CancellationToken token, List<NcNotice> notices)
    {
        notices.Add(new("参数摘要列出源码声明的不同值及首次出现行；刀具直径、余量与实际机床配置未提供。"));
        var lines = new List<string>();
        using (var reader = new StringReader(code))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (lines.Count >= MaxLines) throw new InvalidDataException("NC 程序超过 100 万行，暂不能打开。");
                lines.Add(line);
            }
        }
        var parameters = ReadParameters(lines, token, notices);
        NcPlaybackProgram? program = null; string? playbackError = null;
        try { program = NcPlaybackParser.ParseExternal(code, token, notices); }
        catch (InvalidDataException error) { playbackError = error.Message; }
        if (program is not null)
        {
            notices.Add(new($"预览显示 {program.WorkCoordinateSystem} 中的编程坐标，不包含机床工件偏置、刀长表及换刀自动移动；空移速度和时间为估算。"));
            if (program.InitialPosition is { IsZKnown: false } && !notices.Any(n => n.Message.Contains("未给出 Z", StringComparison.Ordinal)))
                notices.Add(new("程序未给出 Z，显示 XY 位置；Z 和实际加工深度未指定。"));
        }
        return new(sourcePath, code, lines.AsReadOnly(), parameters.AsReadOnly(), notices.AsReadOnly(), program, playbackError);
    }

    private static List<NcParameter> ReadParameters(List<string> lines, CancellationToken token, List<NcNotice> notices)
    {
        var result = new List<NcParameter>(); var unique = new HashSet<(string, string)>();
        var scale = 1d; var feedMode = 94d; var omitted = false; var hasPause = false;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            token.ThrowIfCancellationRequested();
            List<(char Letter, double Value)> words;
            try
            {
                var block = NcPlaybackParser.StripComments(lines[lineIndex], lineIndex);
                if (block.Length == 0 || block == "%") continue;
                words = NcPlaybackParser.ReadWords(block, lineIndex);
            }
            catch (InvalidDataException) { continue; }
            // Parameters remain source declarations even when an unsupported instruction prevents playback.
            var units = words.LastOrDefault(w => w.Letter == 'G' && w.Value is 20 or 21);
            if (units.Letter == 'G') scale = units.Value == 20 ? 25.4 : 1;
            var feedWord = words.LastOrDefault(w => w.Letter == 'G' && w.Value is 93 or 94 or 95);
            if (feedWord.Letter == 'G') feedMode = feedWord.Value;
            foreach (var (letter, value) in words)
            {
                string? name = null; var text = NcPlaybackParser.Number(value);
                switch (letter)
                {
                    case 'O': name = "程序号 O"; break;
                    case 'T': name = "刀号 T（程序指定）"; break;
                    case 'H': name = "刀长补偿号 H"; break;
                    case 'S': name = "主轴设定 S"; break;
                    case 'F':
                        name = "进给 F";
                        text += feedMode == 93 ? " min⁻¹（G93）" : feedMode == 95 ? (scale == 1 ? " mm/rev（G95）" : " in/rev（G95）") : scale == 1 ? " mm/min" : " in/min（" + NcPlaybackParser.Number(value * scale) + " mm/min）";
                        break;
                    case 'G':
                        if (value is 20 or 21) { name = "编程单位"; text = value == 20 ? "G20 英寸" : "G21 毫米"; }
                        else if (value is 17 or 18 or 19) { name = "加工平面"; text = "G" + text; }
                        else if (value is 90 or 91) { name = "终点坐标方式"; text = value == 90 ? "G90 绝对" : "G91 相对"; }
                        else if (value is 90.1 or 91.1) { name = "圆心坐标方式"; text = value == 90.1 ? "G90.1 绝对" : "G91.1 相对"; }
                        else if (value is 54 or 55 or 56 or 57 or 58 or 59) { name = "工件坐标系"; text = "G" + text; }
                        else if (value is 93 or 94 or 95) { name = "进给方式"; text = "G" + text; }
                        break;
                    case 'M':
                        if (value is 3 or 4 or 5) { name = "主轴状态"; text = value == 3 ? "M3 顺时针" : value == 4 ? "M4 逆时针" : "M5 停止"; }
                        if (value is 0 or 1) hasPause = true;
                        break;
                }
                if (name is null || unique.Contains((name, text))) continue;
                if (result.Count == MaxParameterValues) { omitted = true; continue; }
                unique.Add((name, text));
                result.Add(new(name, text, lineIndex));
            }
        }
        if (omitted) notices.Add(new("参数摘要最多显示 10000 个不同值，其余请在源码中查看。"));
        if (hasPause) notices.Add(new("程序包含 M0/M1 暂停，估算时间不包含人工停留时间。"));
        return result;
    }

    private static (string Code, string? Notice) Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return (new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), null);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return (new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2), null);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return (new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2), null);
        try { return (new UTF8Encoding(false, true).GetString(bytes), null); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return (Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes), "未检测到 UTF 编码，按 GB18030 读取文本。"); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("无法识别 NC 文本编码，请使用 UTF-8、UTF-16 或 GB18030。", error); }
        }
    }
}
