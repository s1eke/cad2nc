using System.Collections.Immutable;

namespace Cad2Nc.Core;

/// <summary>One independently generated and validated NC program for one physical motherboard.</summary>
public sealed record BatchNcProgram(
    string SheetContourId,
    string SuggestedFileName,
    ToolpathPlan Plan,
    string Code,
    NcPlaybackProgram Playback)
{
    public int RouteCount => Plan.Toolpaths.Length;
}

/// <summary>Result of splitting a CAD document into motherboard-scoped NC programs.</summary>
public sealed record BatchNcGenerationResult(
    ImmutableArray<BatchNcProgram> Programs,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool Succeeded => !HasErrors && !Programs.IsDefaultOrEmpty && Programs.Length > 0;
}

/// <summary>
/// Splits one drawing containing multiple physical stock rectangles into independent NC
/// programs. Each planner invocation sees only its own stock and the contours it contains,
/// so clearances and nesting cannot accidentally cross a board boundary.
/// </summary>
public static class BatchNcGenerator
{
    public static BatchNcGenerationResult Generate(
        CadDocument document,
        ConversionConfig config,
        IReadOnlyDictionary<string, ContourSelection>? selection = null,
        string? fileStem = null,
        long revision = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(document.Diagnostics);
        var sheets = ToolpathPlanner.FindSheetCandidates(document);
        if (sheets.Count == 0)
        {
            diagnostics.Add(new("batch.no_sheets", "未找到可用于分板的轴向矩形母板。", DiagnosticSeverity.Error,
                RelatedField: "SheetContourId"));
            return new([], diagnostics.ToImmutable());
        }

        var groups = sheets.ToDictionary(sheet => sheet.Id,
            _ => new List<Contour>(), StringComparer.Ordinal);
        var sheetGeometry = sheets.ToDictionary(sheet => sheet.Id, sheet => sheet.Geometry,
            StringComparer.Ordinal);

        // Assign every non-stock contour to exactly one board. An overlap is unsafe to
        // guess, while an orphan contour is reported so the caller can fix the drawing.
        foreach (var contour in document.Contours.Where(c => !sheets.Any(s => s.Id == c.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = sheets.Where(sheet => sheetGeometry[sheet.Id].Covers(contour.Geometry)).ToArray();
            if (matches.Length == 1)
            {
                groups[matches[0].Id].Add(contour);
            }
            else if (matches.Length == 0)
            {
                var requested = selection?.GetValueOrDefault(contour.Id) ?? document.DefaultSelection[contour.Id];
                if (requested.Included)
                    diagnostics.Add(new("batch.unassigned_contour", $"轮廓 {contour.Id} 不在任何母板内，无法确定输出文件。",
                        DiagnosticSeverity.Error, contour.Id, RelatedField: "SheetContourId"));
            }
            else
            {
                diagnostics.Add(new("batch.ambiguous_contour", $"轮廓 {contour.Id} 同时位于多个母板内，无法自动分组。",
                    DiagnosticSeverity.Error, contour.Id, RelatedField: "SheetContourId",
                    RelatedContourIds: matches.Select(s => s.Id).ToImmutableArray()));
            }
        }

        // Overlapping stock rectangles make containment ambiguous even when no part is
        // currently inside the overlap; reject them before generating any partial output.
        for (var i = 0; i < sheets.Count; i++)
        for (var j = i + 1; j < sheets.Count; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Touching stock edges are valid in a CAD layout; only a positive-area
            // intersection makes board assignment ambiguous.
            if (sheetGeometry[sheets[i].Id].Intersection(sheetGeometry[sheets[j].Id]).Area > 1e-9)
                diagnostics.Add(new("batch.overlapping_sheets",
                    $"母板 {sheets[i].Id}/{sheets[j].Id} 相交，无法安全分组。", DiagnosticSeverity.Error,
                    RelatedField: "SheetContourId", RelatedContourIds: [sheets[i].Id, sheets[j].Id]));
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new([], diagnostics.ToImmutable());

        var programs = ImmutableArray.CreateBuilder<BatchNcProgram>();
        var stem = SanitizeStem(fileStem ?? Path.GetFileNameWithoutExtension(document.SourcePath));
        if (string.IsNullOrEmpty(stem)) stem = "cad2nc";

        foreach (var sheet in sheets.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contours = groups[sheet.Id];
            var scopedContours = ImmutableArray.CreateBuilder<Contour>(contours.Count + 1);
            scopedContours.Add(sheet);
            scopedContours.AddRange(contours);
            var scopedDocument = document with
            {
                SourceHash = document.SourceHash + "/sheet/" + sheet.Id,
                Contours = scopedContours.ToImmutable(),
            };
            var selected = ImmutableDictionary.CreateBuilder<string, ContourSelection>(StringComparer.Ordinal);
            selected[sheet.Id] = new(false, CutSide.Outside);
            foreach (var contour in contours)
                selected[contour.Id] = selection?.GetValueOrDefault(contour.Id)
                    ?? document.DefaultSelection[contour.Id];

            var setup = new WorkpieceSetup { SheetContourId = sheet.Id };
            var request = new PlanningRequest(scopedDocument, config, selected.ToImmutable(), revision, setup);
            try
            {
                var plan = ToolpathPlanner.Generate(request, cancellationToken);
                if (plan.Toolpaths.IsEmpty)
                {
                    diagnostics.Add(new("batch.empty_board", $"母板 {sheet.Id} 没有选中的可加工轮廓。",
                        DiagnosticSeverity.Warning, sheet.Id, RelatedField: "Contours"));
                    continue;
                }
                // Preserve plan-level safety guidance in the batch result as
                // well as on each generated program.  Warnings (notably the
                // workholding notice) must remain visible in CLI summaries
                // without turning a valid board into a failed batch.
                diagnostics.AddRange(plan.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error));
                var code = NcGenerator.Generate(plan, config);
                var playback = NcPlaybackParser.ParseGenerated(code, config);
                var index = programs.Count + 1;
                programs.Add(new(sheet.Id, $"{stem}_{index:00}.nc", plan, code, playback));
            }
            catch (ToolpathGenerationException error)
            {
                diagnostics.AddRange(error.Diagnostics);
                diagnostics.Add(new("batch.board_failed", $"母板 {sheet.Id} 生成失败：{error.Message}",
                    DiagnosticSeverity.Error, sheet.Id, RelatedField: "SheetContourId"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                diagnostics.Add(new("batch.board_failed", $"母板 {sheet.Id} 生成失败：{error.Message}",
                    DiagnosticSeverity.Error, sheet.Id, RelatedField: "SheetContourId"));
            }
        }

        return new(programs.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>Writes every program atomically after validating the complete batch.</summary>
    public static ImmutableArray<string> Write(string directory, BatchNcGenerationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(result);
        if (result.HasErrors) throw new InvalidOperationException("批量刀路包含错误，未写出任何 NC 文件。");
        if (!result.Succeeded) throw new InvalidOperationException("没有可写出的 NC 文件。");
        Directory.CreateDirectory(directory);
        var paths = ImmutableArray.CreateBuilder<string>(result.Programs.Length);
        foreach (var program in result.Programs)
        {
            var path = Path.Combine(directory, program.SuggestedFileName);
            NcGenerator.Write(path, program.Plan);
            paths.Add(Path.GetFullPath(path));
        }
        return paths.ToImmutable();
    }

    private static string SanitizeStem(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }
}
