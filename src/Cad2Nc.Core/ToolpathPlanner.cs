using System.Collections.Immutable;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Buffer;

namespace Cad2Nc.Core;

/// <summary>Plans explicitly classified contours as pre-compensated cutter-centre routes.</summary>
public static class ToolpathPlanner
{
    public const int MaxSelectedContours = 2_000;
    private const double AxisResolution = RouteGeometryHelper.AxisResolution;

    private sealed record Source(Contour Contour, Polygon Geometry, ContourSelection Selection, bool IsSheet)
    {
        public string Id => Contour.Id;
        public bool Inside => Selection.Side == CutSide.Inside;
        // An explicitly trusted SheetContourId is a motherboard even when an
        // importer did not mark that contour as an outer candidate.  Keep this
        // classification consistent for ordering and nesting/clearance checks.
        public bool OuterCandidate => Contour.IsOuterCandidate || IsSheet;
    }
    private sealed record Profile(Source Source, Polygon Geometry);
    private sealed record StableSheet(string Id, Polygon Geometry);
    private sealed record EntryCandidate(Point2 Point, Point2 QuantizedPoint, bool IsVertex, int SegmentIndex,
        double Score, Point2? ReleasePoint, ImmutableArray<Point2> Route);

    public static ToolpathPlan Generate(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentNullException.ThrowIfNull(request.Config);
        ArgumentNullException.ThrowIfNull(request.Selection);
        request.Config.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var fixedOrigin = ResolveFixedOrigin(request);
        var sheetId = request.Config.OriginMode == OriginMode.SheetLowerLeft
            ? ResolveSheetContour(request.Document, request.Workpiece).Id : null;
        // An explicit stock contour is meaningful for stability scoring even
        // when the coordinate origin is manual or follows the toolpath. Only
        // treat it as stock when it is a trusted rectangular contour; a stale
        // or non-rectangular id remains an ordinary contour and is reported by
        // ResolveStableSheet below.
        var configuredSheetId = request.Workpiece?.SheetContourId;
        var knownIds = request.Document.Contours.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (knownIds.Count != request.Document.Contours.Length)
            throw new ToolpathGenerationException("轮廓编号重复，不能生成刀路。");
        var unknown = request.Selection.Keys.Where(id => !knownIds.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            throw new ToolpathGenerationException($"轮廓不存在，未生成部分 NC：{string.Join(", ", unknown)}");
        var sources = request.Document.Contours.Select(contour =>
        {
            var isSheet = contour.Id == sheetId ||
                (sheetId is null && contour.Id == configuredSheetId && IsSheetRectangle(contour));
            var selection = request.Selection.GetValueOrDefault(contour.Id) ?? new ContourSelection(!contour.IsOuterCandidate && !isSheet);
            if (!Enum.IsDefined(selection.Side))
                throw new ToolpathGenerationException($"轮廓 {contour.Id} 的加工侧无效。");
            return new Source(contour, contour.Geometry, selection, isSheet);
        }).ToArray();
        var diagnostics = request.Document.Diagnostics.ToBuilder();
        var selectedCandidates = sources.Where(s => s.Selection.Included).ToArray();
        if (selectedCandidates.Length > MaxSelectedContours)
            throw new ToolpathGenerationException($"选择了 {selectedCandidates.Length} 个轮廓，最多允许 {MaxSelectedContours} 个，未生成 NC。");
        StableSheet? stableSheet = null;
        if (selectedCandidates.Length != 0 && request.Config.Machining.PrioritizePartStability)
            stableSheet = ResolveStableSheet(request, diagnostics);
        var selected = request.Config.Machining.PrioritizePartStability
            ? selectedCandidates
                .OrderBy(s => StabilityRank(s, stableSheet))
                .ThenBy(s => s.Contour.Area)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToArray()
            : selectedCandidates
                .OrderBy(s => s.Inside ? 0 : 1)
                .ThenBy(s => s.Contour.Area)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToArray();
        if (selected.Length == 0)
        {
            diagnostics.Add(new("no_selection", "没有选择加工轮廓，未生成刀路。"));
            return MakePlan(request, [], [], fixedOrigin ?? default, diagnostics.ToImmutable());
        }
        diagnostics.Add(request.Config.Machining.PrioritizePartStability
            ? new("plan.workholding_unmodeled",
                "稳定加工顺序可降低零件提前切断后的移动风险，但软件未建模压板、真空、夹具、桥位或底部薄皮，仍需确认可靠装夹。",
                DiagnosticSeverity.Warning, RequiresConfirmation: false)
            : new("plan.stable_order_disabled",
                "当前使用传统逐轮廓加工顺序，零件可能较早完全切断；请确认装夹能够防止移动。",
                DiagnosticSeverity.Warning, RequiresConfirmation: false));

        try
        {
            var selectedIds = selected.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var source in selected.Where(s => !s.Inside))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var host = ClosestHost(sources, source, covers: false);
                if (host is not null && !selectedIds.Contains(host.Id) && !host.OuterCandidate)
                    diagnostics.Add(new("nested_outside", $"轮廓 {source.Id} 位于未选中的 {host.Id} 内；请确认它是独立零件，而不是内孔。",
                        DiagnosticSeverity.Warning, source.Id, RequiresConfirmation: true, RelatedField: "CutSide", RelatedContourIds: [source.Id, host.Id]));
            }

            var config = request.Config;
            var profiles = new List<Profile>();
            var rawPaths = new List<Toolpath>();
            var failures = new List<Diagnostic>();
            foreach (var source in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finished = Polygons(Offset(source.Geometry,
                    source.Inside ? config.Machining.EdgeAllowance : -config.Machining.EdgeAllowance));
                if (finished.Count != 1 || finished[0].NumInteriorRings != 0)
                {
                    failures.Add(Failure("plan.finished_profile", $"{source.Id} 的成品边在 {config.Machining.EdgeAllowance:g} mm 留量下坍塌、分裂或封闭凹槽", "EdgeAllowance", source.Id));
                    continue;
                }
                var centre = Polygons(Offset(finished[0], source.Inside ? -config.Tool.Radius : config.Tool.Radius));
                if (centre.Count != 1 || centre[0].NumInteriorRings != 0)
                {
                    failures.Add(Failure("plan.tool_fit", $"{source.Id} 的刀心路线在直径 {config.Tool.Diameter:g} mm 刀具下坍塌、分裂或封闭凹槽", "Diameter", source.Id));
                    continue;
                }
                if (LosesNarrowFeature(finished[0], centre[0], config.Tool.Radius, source.Inside))
                {
                    failures.Add(Failure("plan.tool_fit", $"{source.Id} 存在直径 {config.Tool.Diameter:g} mm 刀具无法进入的窄小特征", "Diameter", source.Id));
                    continue;
                }
                var ring = DirectedRing(centre[0], config.Machining.CutDirection == CutDirection.Counterclockwise);
                if (ring.Length < 4)
                {
                    failures.Add(Failure("plan.tool_fit", $"{source.Id} 生成了退化刀路", "Diameter", source.Id));
                    continue;
                }
                profiles.Add(new(source, finished[0]));
                rawPaths.Add(new(source.Id, source.Contour.Layer,
                    ring.Select(c => new Point2(c.X, c.Y)).ToImmutableArray(),
                    source.Inside ? -config.NetToolOffset : config.NetToolOffset, config.Machining.CutDirection));
            }
            if (failures.Count != 0)
                throw new ToolpathGenerationException($"部分选中轮廓无法加工，未生成部分 NC：{string.Join("；", failures.Select(d => d.Message))}。", failures);
            failures = OutsideClearanceFailures(sources, profiles, config.Tool.Diameter, cancellationToken);
            if (failures.Count != 0)
                throw new ToolpathGenerationException($"外轮廓的加工侧或刀具间隙不满足要求，未生成部分 NC：{string.Join("；", failures.Select(d => d.Message))}。", failures);
            failures = InsideRelationshipFailures(sources, profiles, config, cancellationToken);
            if (failures.Count != 0)
                throw new ToolpathGenerationException($"内孔未被安全包含，未生成部分 NC：{string.Join("；", failures.Select(d => d.Message))}。", failures);
            if (rawPaths.Count == 0) throw new ToolpathGenerationException("选中的轮廓均未产生可加工刀路。");

            var origin = fixedOrigin ?? new Point2(rawPaths.Min(p => p.Points.Min(c => c.X)), rawPaths.Min(p => p.Points.Min(c => c.Y)));
            if (config.Machining.PrioritizePartStability)
                rawPaths = ChooseStableEntries(rawPaths, profiles, stableSheet, origin, config, diagnostics,
                    cancellationToken);
            var normalized = rawPaths.Select(path => path with
            {
                Points = path.Points.Select(p => new Point2(CleanZero(p.X - origin.X), CleanZero(p.Y - origin.Y))).ToImmutableArray(),
            }).ToImmutableArray();
            var finishedProfiles = profiles.Select(p => new FinishedProfile(p.Source.Id,
                p.Geometry.ExteriorRing.Coordinates.Select(c => new Point2(c.X, c.Y)).ToImmutableArray())).ToImmutableArray();
            cancellationToken.ThrowIfCancellationRequested();
            return MakePlan(request, normalized, finishedProfiles, origin, diagnostics.ToImmutable());
        }
        catch (Exception error) when (error is not ToolpathGenerationException and not OperationCanceledException)
        {
            throw new ToolpathGenerationException($"几何计算未能完整完成，未生成部分 NC：{error.Message}");
        }
    }

    /// <summary>Only importer-designated, axis-aligned rectangular stock is selected automatically.</summary>
    public static IReadOnlyList<Contour> FindSheetCandidates(CadDocument document) => document.Contours
        .Where(c => c.IsOuterCandidate && IsSheetRectangle(c)).OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();

    public static Contour ResolveSheetContour(CadDocument document, WorkpieceSetup? workpiece = null)
    {
        if (!string.IsNullOrWhiteSpace(workpiece?.SheetContourId))
        {
            var contour = document.Contours.FirstOrDefault(c => c.Id == workpiece.SheetContourId);
            if (contour is not null && IsSheetRectangle(contour)) return contour;
            throw new ToolpathGenerationException("指定母板不存在或不是轴向矩形，请重新指定母板或手动原点。",
                [Failure("plan.sheet_origin", "指定母板不存在或不是轴向矩形", "SheetContourId")]);
        }
        var candidates = FindSheetCandidates(document);
        if (candidates.Count == 1) return candidates[0];
        throw new ToolpathGenerationException(candidates.Count == 0
            ? "未找到明确的轴向矩形母板，请指定母板或手动原点。"
            : "存在多个母板候选，请指定母板或手动原点。",
            [Failure("plan.sheet_origin", "请指定唯一母板或手动原点", "SheetContourId")]);
    }

    /// <summary>Returns a stable CAD-space origin, or null for the legacy route minimum.</summary>
    public static Point2? ResolveFixedOrigin(PlanningRequest request)
    {
        if (request.Config.OriginMode == OriginMode.ToolpathMinimum) return null;
        if (request.Config.OriginMode == OriginMode.SheetLowerLeft)
        {
            var sheet = ResolveSheetContour(request.Document, request.Workpiece);
            return new(sheet.Bounds.MinX, sheet.Bounds.MinY);
        }
        if (request.Config.OriginMode == OriginMode.Manual && request.Workpiece?.ManualOrigin is { } point &&
            double.IsFinite(point.X) && double.IsFinite(point.Y)) return point;
        throw new ToolpathGenerationException("请填写有效的 CAD 手动原点 X/Y。",
            [Failure("plan.manual_origin", "请填写有限的手动原点坐标", "ManualOrigin")]);
    }

    public static bool IsSheetRectangle(Contour contour)
    {
        var polygon = contour.Geometry;
        var bounds = contour.Bounds;
        if (polygon.NumInteriorRings != 0 || bounds.Width <= 0 || bounds.Height <= 0) return false;
        // Every edge must lie on an envelope side, rather than merely being orthogonal:
        // an almost-rectangular notched polygon is not a trustworthy stock reference.
        const double tolerance = 1e-8;
        bool On(double coordinate, double boundary) => Math.Abs(coordinate - boundary) <= tolerance;
        return contour.Points.Zip(contour.Points.Skip(1), (a, b) =>
            On(a.X, bounds.MinX) && On(b.X, bounds.MinX) ||
            On(a.X, bounds.MaxX) && On(b.X, bounds.MaxX) ||
            On(a.Y, bounds.MinY) && On(b.Y, bounds.MinY) ||
            On(a.Y, bounds.MaxY) && On(b.Y, bounds.MaxY)).All(onBoundary => onBoundary);
    }

    private static Source? ClosestHost(IEnumerable<Source> sources, Source child, bool covers) => sources
        .Where(s => s.Id != child.Id && !s.Inside && (covers ? s.Geometry.Covers(child.Geometry) : s.Geometry.Contains(child.Geometry)))
        .OrderBy(s => s.Contour.Area).ThenBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault();

    private static List<Diagnostic> OutsideClearanceFailures(Source[] sources, List<Profile> profiles,
        double diameter, CancellationToken cancellationToken)
    {
        var outside = profiles.Where(p => !p.Source.Inside).ToArray();
        var failures = new List<Diagnostic>();
        var tolerance = Math.Max(1e-7, diameter * 1e-9);
        var selectedIds = outside.Select(p => p.Source.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in outside)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = ClosestHost(sources, profile.Source, covers: false);
            if (host is not null && selectedIds.Contains(host.Id) && !host.OuterCandidate)
                failures.Add(Failure("plan.outside_clearance", $"{profile.Source.Id} 嵌套在非母板外轮廓 {host.Id} 中；如果它是孔，请设为内侧加工", "CutSide", profile.Source.Id, host.Id));
        }
        var tree = new STRtree<int>();
        for (var i = 0; i < outside.Length; i++) tree.Insert(outside[i].Geometry.EnvelopeInternal, i);
        for (var i = 0; i < outside.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = outside[i];
            foreach (var otherIndex in tree.Query(ExpandedEnvelope(first.Geometry, diameter + tolerance)))
            {
                if (otherIndex <= i) continue;
                var second = outside[otherIndex];
                var clearance = first.Geometry.Boundary.Distance(second.Geometry.Boundary);
                if (clearance + tolerance < diameter)
                    failures.Add(Failure("plan.outside_clearance", $"{first.Source.Id}/{second.Source.Id} 的成品边间隙仅 {clearance:F3} mm", "Diameter", first.Source.Id, second.Source.Id));
            }
        }
        // A deselected sibling is protected material, not expendable scrap.
        var unselected = sources.Where(s => !s.Inside && !selectedIds.Contains(s.Id)).ToArray();
        var unselectedTree = new STRtree<Source>();
        foreach (var source in unselected) unselectedTree.Insert(source.Geometry.EnvelopeInternal, source);
        foreach (var profile in outside)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var other in unselectedTree.Query(ExpandedEnvelope(profile.Geometry, diameter + tolerance)))
            {
                if (other.Geometry.Covers(profile.Source.Geometry) || profile.Source.Geometry.Covers(other.Geometry)) continue;
                var clearance = profile.Geometry.Boundary.Distance(other.Geometry);
                if (clearance + tolerance < diameter)
                    failures.Add(Failure("plan.outside_clearance", $"{profile.Source.Id}/未选轮廓 {other.Id} 的间隙仅 {clearance:F3} mm", "Diameter", profile.Source.Id, other.Id));
            }
        }
        return failures;
    }

    private static List<Diagnostic> InsideRelationshipFailures(Source[] sources, List<Profile> profiles,
        ConversionConfig config, CancellationToken cancellationToken)
    {
        var inside = profiles.Where(p => p.Source.Inside).ToArray();
        var failures = new List<Diagnostic>();
        var tolerance = Math.Max(1e-7, config.Tool.Diameter * 1e-9);
        var tree = new STRtree<int>();
        for (var i = 0; i < inside.Length; i++) tree.Insert(inside[i].Geometry.EnvelopeInternal, i);
        for (var i = 0; i < inside.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = inside[i];
            foreach (var otherIndex in tree.Query(ExpandedEnvelope(first.Geometry, tolerance)))
            {
                if (otherIndex <= i) continue;
                var second = inside[otherIndex];
                if (first.Geometry.Intersects(second.Geometry) || first.Geometry.Distance(second.Geometry) <= tolerance)
                    failures.Add(Failure("plan.inside_relationship", $"孔 {first.Source.Id}/{second.Source.Id} 的成品边相接或重叠", "CutSide", first.Source.Id, second.Source.Id));
            }
            var host = ClosestHost(sources, first.Source, covers: true);
            if (host is null)
            {
                failures.Add(Failure("plan.inside_relationship", $"孔 {first.Source.Id} 没有包含它的外轮廓", "CutSide", first.Source.Id));
                continue;
            }
            var hostFinished = Polygons(Offset(host.Geometry, -config.Machining.EdgeAllowance));
            if (hostFinished.Count != 1 || !hostFinished[0].Contains(first.Geometry))
                failures.Add(Failure("plan.inside_relationship", $"孔 {first.Source.Id} 穿透了外轮廓 {host.Id} 的成品边", "EdgeAllowance", first.Source.Id, host.Id));
        }
        return failures;
    }

    /// <summary>
    /// Resolves a stock contour for stability scoring without making stock
    /// discovery a planning error.  Unlike the sheet-origin path, a missing or
    /// ambiguous stock only means that the sheet-edge part of the score is
    /// unavailable; other selected finished edges remain useful references.
    /// </summary>
    private static StableSheet? ResolveStableSheet(PlanningRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var requestedId = request.Workpiece?.SheetContourId;
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var explicitContour = request.Document.Contours.FirstOrDefault(c => c.Id == requestedId);
            if (explicitContour is not null && IsSheetRectangle(explicitContour))
                return new(explicitContour.Id, explicitContour.Geometry);

            AddStableSheetDiagnostic(diagnostics,
                "指定母板不存在或不是可信的轴向矩形；安全下刀点仍会参考其他成品边，但无法评估板边距离。",
                requestedId is null ? [] : [requestedId]);
            return null;
        }

        var candidates = FindSheetCandidates(request.Document);
        if (candidates.Count == 1)
            return new(candidates[0].Id, candidates[0].Geometry);

        var message = candidates.Count == 0
            ? "未找到唯一可信的母板边界；安全下刀点仍会参考其他成品边，但无法评估板边距离。"
            : "存在多个母板候选且未指定母板；安全下刀点仍会参考其他成品边，但无法确定板边距离。";
        AddStableSheetDiagnostic(diagnostics, message, candidates.Select(c => c.Id).ToImmutableArray());
        return null;
    }

    private static void AddStableSheetDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics,
        string message, ImmutableArray<string> contourIds)
    {
        // Importers may already have supplied a same-code warning. Keep one
        // planner diagnostic per generation so the warning is not duplicated.
        if (diagnostics.Any(d => d.Code == "plan.stable_sheet_unknown")) return;
        diagnostics.Add(new("plan.stable_sheet_unknown", message, DiagnosticSeverity.Warning,
            RequiresConfirmation: false, RelatedField: "SheetContourId", RelatedContourIds: contourIds));
    }

    private static int StabilityRank(Source source, StableSheet? sheet) =>
        source.Inside ? 0 : (source.OuterCandidate || sheet?.Id == source.Id ? 2 : 1);

    /// <summary>
    /// Re-rotates each completed route to a point whose worst clearance is as
    /// large as possible.  Rotation (and an optional exact segment midpoint)
    /// leaves the directed contour unchanged; no geometry is re-offset here.
    /// </summary>
    private static List<Toolpath> ChooseStableEntries(IReadOnlyList<Toolpath> paths,
        IReadOnlyList<Profile> profiles, StableSheet? sheet, Point2 origin, ConversionConfig config,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        var chosen = new List<Toolpath>(paths.Count);
        for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[pathIndex];
            var references = profiles
                .Where((_, index) => index != pathIndex)
                .Select(profile => profile.Geometry.Boundary)
                .ToArray();
            var canScore = references.Length != 0 || sheet is not null;
            if (!canScore)
            {
                AddEntryFallbackDiagnostic(diagnostics, path.ContourId,
                    "没有可评分的安全参考几何，保留原左下角起点。");
                chosen.Add(path);
                continue;
            }

            var candidates = BuildEntryCandidates(path, origin, config, references, sheet,
                cancellationToken).ToArray();
            var best = candidates
                .Where(candidate => double.IsFinite(candidate.Score))
                // Quantize the score before ordering.  This keeps a sub-micron
                // floating-point difference from changing a deterministic
                // vertex/coordinate tie-break, including for negative
                // signed distances outside the stock.
                .OrderByDescending(candidate => RouteGeometryHelper.Quantize(candidate.Score))
                .ThenByDescending(candidate => candidate.IsVertex)
                .ThenBy(candidate => candidate.QuantizedPoint.X)
                .ThenBy(candidate => candidate.QuantizedPoint.Y)
                .ThenBy(candidate => candidate.Point.X)
                .ThenBy(candidate => candidate.Point.Y)
                .ThenBy(candidate => candidate.SegmentIndex)
                .FirstOrDefault();
            if (best is null || best.Route.IsDefaultOrEmpty)
            {
                AddEntryFallbackDiagnostic(diagnostics, path.ContourId,
                    "候选下刀点在 0.001 mm 量化后全部退化，保留原左下角起点。");
                chosen.Add(path);
                continue;
            }
            chosen.Add(path with { Points = best.Route });
        }
        return chosen;
    }

    private static void AddEntryFallbackDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics,
        string contourId, string reason)
    {
        diagnostics.Add(new("plan.stable_entry_fallback", $"轮廓 {contourId}{reason}",
            DiagnosticSeverity.Warning, contourId, RequiresConfirmation: false,
            RelatedField: "PrioritizePartStability", RelatedContourIds: [contourId]));
    }

    private static IEnumerable<EntryCandidate> BuildEntryCandidates(Toolpath path, Point2 origin,
        ConversionConfig config, IReadOnlyList<Geometry> references, StableSheet? sheet,
        CancellationToken cancellationToken)
    {
        var points = path.Points;
        if (points.Length < 4 || points[0] != points[^1]) yield break;
        var openCount = points.Length - 1;
        for (var vertexIndex = 0; vertexIndex < openCount; vertexIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = RotateAtVertex(points, vertexIndex);
            // The NC writer rounds every axis to 0.001 mm.  Do the same
            // closed-route validation while scoring candidates so a route that
            // is entirely (or partially) collapsed never masquerades as a
            // valid stable entry.  If every candidate fails this check the
            // caller keeps the legacy lower-left entry and reports fallback.
            if (!RouteGeometryHelper.TryQuantizeClosedRoute(route
                    .Select(point => new Point2(point.X - origin.X, point.Y - origin.Y)).ToArray(), out _))
                continue;
            var candidate = points[vertexIndex];
            // Stable-entry scoring is performed in absolute CAD coordinates.
            // Output-route validation and ramp stepping remain origin-relative
            // because those operations must match the emitted NC coordinates.
            var quantized = RouteGeometryHelper.Quantize(candidate);
            var release = ReleasePoint(route, origin, config);
            if (config.Machining.EntryMode == EntryMode.Ramp && release is null) continue;
            var score = EntryScore(candidate, release, path.ContourId, config, references, sheet);
            yield return new(candidate, quantized, true, vertexIndex, score, release, route);
        }

        for (var segmentIndex = 0; segmentIndex < openCount; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = points[segmentIndex];
            var b = points[segmentIndex + 1];
            var midpoint = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var qa = RouteGeometryHelper.Quantize(new Point2(a.X - origin.X, a.Y - origin.Y));
            var qb = RouteGeometryHelper.Quantize(new Point2(b.X - origin.X, b.Y - origin.Y));
            var qm = RouteGeometryHelper.Quantize(new Point2(midpoint.X - origin.X, midpoint.Y - origin.Y));
            // A midpoint which lands on an endpoint after NC quantization would
            // create a zero-length move.  Reject it before inserting it into the
            // route; valid midpoints are kept at their exact CAD-space position.
            if (qa == qb || qm == qa || qm == qb) continue;
            var route = RotateAtSegmentMidpoint(points, segmentIndex, midpoint);
            if (!RouteGeometryHelper.TryQuantizeClosedRoute(route
                    .Select(point => new Point2(point.X - origin.X, point.Y - origin.Y)).ToArray(), out _))
                continue;
            var quantized = RouteGeometryHelper.Quantize(midpoint);
            var release = ReleasePoint(route, origin, config);
            if (config.Machining.EntryMode == EntryMode.Ramp && release is null) continue;
            var score = EntryScore(midpoint, release, path.ContourId, config, references, sheet);
            yield return new(midpoint, quantized, false, segmentIndex, score, release, route);
        }
    }

    private static Point2? ReleasePoint(ImmutableArray<Point2> route, Point2 origin,
        ConversionConfig config)
    {
        if (config.Machining.EntryMode != EntryMode.Ramp) return null;
        var normalized = route
            .Select(point => new Point2(point.X - origin.X, point.Y - origin.Y))
            .ToArray();
        return RouteGeometryHelper.TryAdvanceAlongClosedRoute(normalized, config.Machining.RampLength,
            out var position, out var hasMovement) && hasMovement
            ? ToCadCoordinates(position.Point, origin)
            : null;
    }

    private static double EntryScore(Point2 start, Point2? release, string contourId,
        ConversionConfig config, IReadOnlyList<Geometry> references, StableSheet? sheet)
    {
        var startScore = PointSafetyScore(start, contourId, references, sheet);
        if (!double.IsFinite(startScore)) return double.NaN;
        if (config.Machining.EntryMode != EntryMode.Ramp) return startScore;
        if (release is not { } releasePoint) return double.NaN;
        var releaseScore = PointSafetyScore(releasePoint, contourId, references, sheet);
        return double.IsFinite(releaseScore) ? Math.Min(startScore, releaseScore) : double.NaN;
    }

    private static double PointSafetyScore(Point2 point, string contourId,
        IReadOnlyList<Geometry> references, StableSheet? sheet)
    {
        var coordinate = new Coordinate(point.X, point.Y);
        var pointGeometry = new GeometryFactory().CreatePoint(coordinate);
        var score = double.PositiveInfinity;
        foreach (var reference in references)
            score = Math.Min(score, reference.Distance(pointGeometry));
        if (sheet is not null)
        {
            var distance = sheet.Geometry.Boundary.Distance(pointGeometry);
            score = Math.Min(score, sheet.Geometry.Covers(pointGeometry) ? distance : -distance);
        }
        return score;
    }

    private static ImmutableArray<Point2> RotateAtVertex(IReadOnlyList<Point2> points, int vertexIndex)
    {
        var openCount = points.Count - 1;
        var result = ImmutableArray.CreateBuilder<Point2>(openCount + 1);
        for (var offset = 0; offset < openCount; offset++)
            result.Add(points[(vertexIndex + offset) % openCount]);
        result.Add(result[0]);
        return result.ToImmutable();
    }

    private static ImmutableArray<Point2> RotateAtSegmentMidpoint(IReadOnlyList<Point2> points,
        int segmentIndex, Point2 midpoint)
    {
        var openCount = points.Count - 1;
        var result = ImmutableArray.CreateBuilder<Point2>(openCount + 2);
        result.Add(midpoint);
        for (var offset = 1; offset <= openCount; offset++)
            result.Add(points[(segmentIndex + offset) % openCount]);
        result.Add(midpoint);
        return result.ToImmutable();
    }

    private static Point2 ToCadCoordinates(Point2 relative, Point2 origin) =>
        new(CleanZero(relative.X + origin.X), CleanZero(relative.Y + origin.Y));

    private static ToolpathPlan MakePlan(PlanningRequest request, ImmutableArray<Toolpath> paths,
        ImmutableArray<FinishedProfile> finished, Point2 origin, ImmutableArray<Diagnostic> diagnostics)
    {
        var bounds = Bounds2.FromPoints(paths.SelectMany(p => p.Points));
        var stats = new Dictionary<string, double>
        {
            ["source_contour_count"] = paths.Length,
            ["toolpath_count"] = paths.Length,
            ["total_length_mm"] = paths.Sum(p => p.Length),
            ["width_mm"] = bounds.Width,
            ["height_mm"] = bounds.Height,
            ["net_offset_mm"] = request.Config.NetToolOffset,
        };
        return new(request, paths, finished, origin, diagnostics, stats.ToImmutableDictionary());
    }

    private static Diagnostic Failure(string code, string message, string field, params string[] contourIds)
        => new(code, message, DiagnosticSeverity.Error, contourIds.FirstOrDefault(), RelatedField: field,
            RelatedContourIds: contourIds.ToImmutableArray());

    /// <summary>The physical finished edge, before applying the cutter radius.</summary>
    public static Geometry FinishedEdge(Polygon source, double allowance, bool inside) =>
        Offset(source, inside ? allowance : -allowance);

    private static Geometry Offset(Polygon polygon, double distance)
    {
        Geometry geometry = Math.Abs(distance) <= 1e-12
            ? polygon
            : BufferOp.Buffer(polygon, distance, new BufferParameters
            {
                QuadrantSegments = 12,
                JoinStyle = JoinStyle.Round,
                // Preserve short source features before forming the buffer.
                SimplifyFactor = 0,
            });
        return geometry.IsValid ? geometry : GeometryFixer.Fix(geometry);
    }

    private static List<Polygon> Polygons(Geometry geometry) => geometry switch
    {
        Polygon polygon => polygon.IsEmpty ? [] : [polygon],
        MultiPolygon multi => Enumerable.Range(0, multi.NumGeometries)
            .Select(i => (Polygon)multi.GetGeometryN(i)).Where(p => !p.IsEmpty).ToList(),
        _ => [],
    };

    private static bool LosesNarrowFeature(Polygon finished, Polygon centre, double radius, bool inside)
    {
        var reconstructed = Polygons(Offset(centre, inside ? radius : -radius));
        if (reconstructed.Count != 1) return true;
        var difference = inside
            ? finished.Difference(reconstructed[0])
            : reconstructed[0].Difference(finished);
        var areaTolerance = Math.Max(1e-6, radius * radius * 2e-4);
        foreach (var component in Polygons(difference))
        {
            // Ignore polygonal arc slivers smaller than half an output coordinate quantum.
            if (component.Area <= areaTolerance ||
                BufferOp.Buffer(component, -AxisResolution / 2, new BufferParameters(16)).IsEmpty)
                continue;
            var bounds = component.EnvelopeInternal;
            if (component.Area > radius * radius * 0.25 ||
                Math.Max(bounds.Width, bounds.Height) > radius * 2 + AxisResolution)
                return true;
        }
        return false;
    }

    private static Coordinate[] DirectedRing(Polygon polygon, bool counterClockwise)
    {
        var coordinates = polygon.ExteriorRing.Coordinates;
        if (coordinates.Length < 4) return coordinates;
        var open = coordinates[..^1];
        if (Orientation.IsCCW(coordinates) != counterClockwise) Array.Reverse(open);
        var start = 0;
        for (var i = 1; i < open.Length; i++)
            if (open[i].X < open[start].X || open[i].X == open[start].X && open[i].Y < open[start].Y)
                start = i;
        var result = new Coordinate[open.Length + 1];
        for (var i = 0; i < open.Length; i++) result[i] = open[(start + i) % open.Length].Copy();
        result[^1] = result[0].Copy();
        return result;
    }

    private static Envelope ExpandedEnvelope(Geometry geometry, double distance)
    {
        var envelope = new Envelope(geometry.EnvelopeInternal);
        envelope.ExpandBy(distance);
        return envelope;
    }

    private static double CleanZero(double value) => Math.Abs(value) < 5e-12 ? 0 : value;
}
