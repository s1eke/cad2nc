using System.Collections.Immutable;
using System.Text.Json;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

/// <summary>One physical-board label ready for preview or printing.</summary>
public sealed record BoardLabelInfo(
    string BoardId,
    int Index,
    int Total,
    string SourceFileName,
    string NcFileName,
    Bounds2 BoardBounds,
    Point2 Origin,
    int PartCount,
    int RouteCount,
    ImmutableArray<BoardLabelPath> Paths,
    string QrPayload,
    DateTimeOffset PrintedAtUtc)
{
    /// <summary>Human-facing order identifier; defaults to the source drawing stem.</summary>
    public string OrderName { get; init; } = "";
    public string Sequence => $"{Index:00}/{Total:00}";
    public double BoardWidth => BoardBounds.Width;
    public double BoardHeight => BoardBounds.Height;
}

public sealed record BoardLabelPath(
    string Id,
    ImmutableArray<Point2> Points,
    bool IsHighlighted,
    bool IsStock);

public sealed record BoardLabelBatch(ImmutableArray<BoardLabelInfo> Labels)
{
    public bool IsEmpty => Labels.IsDefaultOrEmpty || Labels.Length == 0;
}

/// <summary>Builds deterministic, printer-independent board identity data.</summary>
public static class BoardLabelBuilder
{
    public static BoardLabelBatch BuildSingle(
        CadDocument document,
        ToolpathPlan plan,
        string? ncFileName = null,
        DateTimeOffset? printedAtUtc = null,
        string? orderName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        var when = printedAtUtc ?? DateTimeOffset.UtcNow;
        var sheetId = plan.Request.Workpiece?.SheetContourId;
        var label = BuildLabel(document, plan, sheetId, 1, 1,
            ncFileName ?? Path.GetFileNameWithoutExtension(document.SourcePath) + ".nc", when, orderName);
        return new([label]);
    }

    public static BoardLabelBatch BuildBatch(
        CadDocument document,
        BatchNcGenerationResult result,
        string? selectedSheetContourId = null,
        DateTimeOffset? printedAtUtc = null,
        string? orderName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(result);
        if (result.HasErrors || !result.Succeeded) return new([]);
        var when = printedAtUtc ?? DateTimeOffset.UtcNow;
        var programs = result.Programs
            .OrderBy(program => program.SheetContourId, StringComparer.Ordinal)
            .Where(program => selectedSheetContourId is null ||
                string.Equals(program.SheetContourId, selectedSheetContourId, StringComparison.Ordinal))
            .ToArray();
        var total = result.Programs.Length;
        var labels = programs.Select((program, index) => BuildLabel(document, program.Plan,
            program.SheetContourId, result.Programs.IndexOf(program) + 1, total,
            program.SuggestedFileName, when, orderName)).ToImmutableArray();
        return new(labels);
    }

    private static BoardLabelInfo BuildLabel(
        CadDocument sourceDocument,
        ToolpathPlan plan,
        string? sheetId,
        int index,
        int total,
        string ncFileName,
        DateTimeOffset printedAtUtc,
        string? orderName)
    {
        var scopedDocument = plan.Request.Document;
        var sheet = sheetId is null ? null : scopedDocument.Contours.FirstOrDefault(c => c.Id == sheetId);
        var bounds = sheet?.Bounds ?? plan.Bounds;
        var origin = plan.Origin;
        var selected = plan.Request.Selection;
        var paths = scopedDocument.Contours
            .Where(c => sheetId is null || c.Id == sheetId || bounds.Width <= 0 || bounds.Height <= 0 ||
                boundsContains(bounds, c.Bounds))
            .Select(c => new BoardLabelPath(c.Id, c.Points,
                selected.TryGetValue(c.Id, out var choice) && choice.Included && c.Id != sheetId,
                c.Id == sheetId || c.IsOuterCandidate))
            .ToImmutableArray();
        var partCount = paths.Count(path => path.IsHighlighted && !path.IsStock);
        var boardId = string.IsNullOrWhiteSpace(sheetId) ? "单板" : sheetId;
        var resolvedOrderName = string.IsNullOrWhiteSpace(orderName)
            ? Path.GetFileNameWithoutExtension(sourceDocument.SourcePath)
            : orderName.Trim();
        var qrPayload = JsonSerializer.Serialize(new
        {
            v = 1,
            sourceHash = sourceDocument.SourceHash,
            order = resolvedOrderName,
            board = boardId,
            index,
            total,
            ncFile = Path.GetFileName(ncFileName),
            printedAtUtc = printedAtUtc.ToUniversalTime().ToString("O")
        });
        return new(boardId, index, total, Path.GetFileName(sourceDocument.SourcePath),
            Path.GetFileName(ncFileName), bounds, origin, partCount, plan.Toolpaths.Length,
            paths, qrPayload, printedAtUtc) { OrderName = resolvedOrderName };
    }

    private static bool boundsContains(Bounds2 outer, Bounds2 inner) =>
        inner.MinX >= outer.MinX - 1e-6 && inner.MaxX <= outer.MaxX + 1e-6 &&
        inner.MinY >= outer.MinY - 1e-6 && inner.MaxY <= outer.MaxY + 1e-6;
}
