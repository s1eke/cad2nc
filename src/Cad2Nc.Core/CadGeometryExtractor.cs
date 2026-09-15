using System.Collections.Immutable;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Overlay.Snap;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Precision;

namespace Cad2Nc.Core;

public sealed record ImportLimits
{
    public long MaxDwgFileBytes { get; init; } = 128L * 1024 * 1024;
    public long MaxDxfFileBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxEntityVertices { get; init; } = 250_000;
    public int MaxDocumentVertices { get; init; } = 2_000_000;
    public int MaxMachiningEntities { get; init; } = 50_000;
    public int MaxClosedContours { get; init; } = 2_500;
    public int MaxInsertDepth { get; init; } = 64;
    public long MaxTopologyCandidates { get; init; } = 2_000_000;
    public long MaxTopologyQueryResults { get; init; } = 4_000_000;
    public int MaxDiagnosticBytes { get; init; } = 4 * 1024 * 1024;
    public double MaxSpan { get; init; } = 1_000_000;
    public double MaxAbsoluteCoordinate { get; init; } = 10_000_000_000;
}

public sealed record ImportOptions
{
    public double FlatteningTolerance { get; init; } = 0.05;
    public double SnapTolerance { get; init; } = 0.01;
    public ImportLimits Limits { get; init; } = new();
    internal void Validate()
    {
        if (!double.IsFinite(FlatteningTolerance) || FlatteningTolerance <= 0 || !double.IsFinite(SnapTolerance) || SnapTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(FlatteningTolerance), "Import tolerances must be finite and positive (snap may be zero).");
        if (Limits is null || Limits.MaxEntityVertices < 2 || Limits.MaxDocumentVertices < 2 || Limits.MaxMachiningEntities < 1 ||
            Limits.MaxClosedContours < 1 || Limits.MaxInsertDepth < 1 || Limits.MaxDwgFileBytes < 1 || Limits.MaxDxfFileBytes < 1 ||
            Limits.MaxTopologyCandidates < 1 || Limits.MaxTopologyQueryResults < 1 || Limits.MaxDiagnosticBytes < 1 ||
            !double.IsFinite(Limits.MaxSpan) || Limits.MaxSpan <= 0 || !double.IsFinite(Limits.MaxAbsoluteCoordinate) || Limits.MaxAbsoluteCoordinate <= 0)
            throw new ArgumentOutOfRangeException(nameof(Limits));
    }
}

/// <summary>Deterministic, millimetre-based conversion of CAD primitives to independent closed boundaries.</summary>
public sealed class CadGeometryExtractor
{
    private readonly ImportOptions options;
    private readonly GeometryFactory factory = new();
    public CadGeometryExtractor(ImportOptions? options = null)
    {
        this.options = options ?? new(); this.options.Validate();
    }

    public CadImportResult Extract(ACadSharp.CadDocument drawing, string sourcePath = "", string sourceHash = "",
        IEnumerable<Diagnostic>? diagnostics = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        cancellationToken.ThrowIfCancellationRequested();
        var messages = (diagnostics ?? []).ToList();
        var unitCode = (int)drawing.Header.InsUnits;
        var scale = MillimetreScale(unitCode);
        if (unitCode == 0) messages.Add(new("units.unspecified", "图纸未声明单位，请确认按毫米解释。", DiagnosticSeverity.Warning));
        else if (Math.Abs(scale - 1) > 1e-12) messages.Add(new("units.converted", $"图纸坐标已按 {scale:G12} 倍换算为毫米。", DiagnosticSeverity.Information));
        var flattener = new CadEntityFlattener(options, cancellationToken);
        var lines = flattener.Flatten(drawing, scale);
        if (Math.Abs(flattener.Elevation) > Math.Max(options.FlatteningTolerance, options.SnapTolerance))
            messages.Add(new("geometry.elevation", "同一非零标高的平面图形已投影到 XY；NC 的 Z 由加工深度控制。", RequiresConfirmation: true));
        if (flattener.Ignored.Count > 0)
            messages.Add(new("geometry.ignored", "已忽略非加工实体：" + string.Join("，", flattener.Ignored.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} × {p.Value}")),
                RequiresConfirmation: true, Count: flattener.Ignored.Values.Sum()));
        var polygons = Polygonize(lines, messages, cancellationToken);
        var contours = MarkSheetFrames(polygons, cancellationToken);
        for (var i = 0; i < polygons.Count; i++)
            if (polygons[i].MergedCount > 0)
                messages.Add(new("geometry.duplicate_contour",
                    $"图层「{polygons[i].Layer}」已合并 {polygons[i].MergedCount} 个重复提取的轮廓，保留 {contours[i].Id}，仅加工一次。",
                    DiagnosticSeverity.Information, contours[i].Id, Count: polygons[i].MergedCount));
        if (contours.Length == 0) messages.Add(new("geometry.no_contours", "没有找到闭合加工轮廓。"));
        if (contours.Any(c => c.Layer.Contains('\ufffd')))
            messages.Add(new("layers.encoding", "部分图层名称无法正确解码。", RequiresConfirmation: true));
        return new(new(sourcePath, sourceHash, contours, messages.ToImmutableArray(), unitCode == 0));
    }

    internal sealed record RingRecord(string Layer, Polygon Polygon, string[] Types, int MergedCount = 0);

    private List<RingRecord> Polygonize(List<CadLineRecord> records, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var result = new List<RingRecord>();
        var seenRings = new Dictionary<string, string>(StringComparer.Ordinal);
        var minimumIssueLength = Math.Max(options.SnapTolerance * .1, 1e-8);
        foreach (var layerGroup in records.GroupBy(r => r.Layer).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var layer = layerGroup.Key; var sourceLines = layerGroup.ToArray();
            ValidateLineConflicts(sourceLines, minimumIssueLength, token);
            try
            {
                var noded = UnaryUnionOp.Union(sourceLines.Select(r => (Geometry)r.Line).ToArray());
                if (options.SnapTolerance > 0)
                    noded = UnaryUnionOp.Union(GeometrySnapper.SnapToSelf(noded, options.SnapTolerance, false));
                if (CountBranches(noded, minimumIssueLength, token) is var branches && branches > 0)
                    throw new CadImportException($"Machining layer '{layer}' contains {branches} crossing or branching node(s); polygonization would invent cut boundaries.");
                var polygonizer = new Polygonizer(); polygonizer.Add(noded);
                var faces = polygonizer.GetPolygons();
                var issues = polygonizer.GetDangles().Count(g => g.Length > minimumIssueLength) +
                    polygonizer.GetCutEdges().Count(g => g.Length > minimumIssueLength) +
                    polygonizer.GetInvalidRingLines().Count(g => g.Length > minimumIssueLength);
                if (issues > 0)
                    diagnostics.Add(new("geometry.open_linework", $"图层「{layer}」有 {issues} 段 open or invalid linework，未生成轮廓。", RequiresConfirmation: true, Count: issues));
                var types = sourceLines.Select(r => r.EntityType).Distinct().Order(StringComparer.Ordinal).ToArray();
                foreach (var face in faces.Cast<Polygon>())
                {
                    AddRing((LinearRing)face.ExteriorRing);
                    for (var hole = 0; hole < face.NumInteriorRings; hole++) AddRing((LinearRing)face.GetInteriorRingN(hole));
                }
                void AddRing(LinearRing ring)
                {
                    token.ThrowIfCancellationRequested();
                    var polygon = factory.CreatePolygon((LinearRing)ring.Copy());
                    if (!polygon.IsValid) throw new CadImportException($"Layer '{layer}' generated an invalid ring; partial NC output is not allowed.");
                    if (polygon.Area <= Math.Max(options.SnapTolerance * options.SnapTolerance, 1e-10)) return;
                    var normalized = (Polygon)polygon.Copy(); normalized.Normalize();
                    var key = Convert.ToHexString(normalized.AsBinary());
                    if (seenRings.TryGetValue(key, out var previousLayer))
                    {
                        if (previousLayer == layer) return;
                        throw new CadImportException($"Identical closed machining contours on layers '{previousLayer}' and '{layer}'; duplicate cutting is not allowed.");
                    }
                    if (result.Count >= options.Limits.MaxClosedContours) throw new CadImportException("Drawing exceeds the closed contours resource limit.");
                    seenRings.Add(key, layer);
                    if (!Orientation.IsCCW(polygon.ExteriorRing.Coordinates)) polygon = (Polygon)polygon.Reverse();
                    result.Add(new(layer, polygon, types));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (CadImportException) { throw; }
            catch (Exception ex) { throw new CadImportException($"Layer '{layer}' could not be processed completely; stopped to prevent partial NC output.", ex); }
        }
        return MergeDuplicateRings(result.OrderBy(r => r.Layer, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Layer, StringComparer.Ordinal)
            .ThenByDescending(r => r.Polygon.Area).ThenBy(r => r.Polygon.EnvelopeInternal.MinX).ThenBy(r => r.Polygon.EnvelopeInternal.MinY)
            .ThenBy(r => r.Polygon.EnvelopeInternal.MaxX).ThenBy(r => r.Polygon.EnvelopeInternal.MaxY).ToList(), token);
    }

    // Polygonizer can return the same boundary as a face shell and another face's
    // hole with different vertices or floating-point noise. Compare temporary
    // precision-reduced copies, never round the geometry used for machining.
    internal List<RingRecord> MergeDuplicateRings(List<RingRecord> records, CancellationToken token = default)
    {
        var precision = new PrecisionModel(10_000_000d);
        var buckets = new Dictionary<(double, double, double, double), List<(int Index, Polygon Geometry)>>();
        var result = new List<RingRecord>();
        long comparisons = 0;
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            var reduced = GeometryPrecisionReducer.Reduce(record.Polygon, precision);
            // Collapsed or split copies are not evidence of equality.
            if (reduced is not Polygon polygon || polygon.IsEmpty || !polygon.IsValid || polygon.NumInteriorRings != 0)
            {
                result.Add(record);
                continue;
            }
            var bounds = polygon.EnvelopeInternal;
            var key = (bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
            var duplicateIndex = -1;
            foreach (var candidate in bucket)
            {
                token.ThrowIfCancellationRequested();
                if (++comparisons > options.Limits.MaxTopologyCandidates)
                    throw new CadImportException("Contour comparisons exceed the safe topology budget.");
                if (!polygon.EqualsTopologically(candidate.Geometry)) continue;
                var previous = result[candidate.Index];
                if (previous.Layer != record.Layer)
                    throw new CadImportException($"Identical closed machining contours on layers '{previous.Layer}' and '{record.Layer}'; duplicate cutting is not allowed.");
                duplicateIndex = candidate.Index;
                result[duplicateIndex] = previous with
                {
                    MergedCount = previous.MergedCount + record.MergedCount + 1,
                    Types = previous.Types.Concat(record.Types).Distinct().Order(StringComparer.Ordinal).ToArray()
                };
                break;
            }
            if (duplicateIndex >= 0) continue;
            bucket.Add((result.Count, polygon));
            result.Add(record);
        }
        return result;
    }

    private void ValidateLineConflicts(CadLineRecord[] records, double minimumOverlap, CancellationToken token)
    {
        var tree = new STRtree<int>();
        for (var i = 0; i < records.Length; i++) tree.Insert(records[i].Line.EnvelopeInternal, i);
        tree.Build(); long candidates = 0, queryResults = 0;
        for (var i = 0; i < records.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var current = records[i]; var hits = tree.Query(current.Line.EnvelopeInternal);
            queryResults += hits.Count;
            if (queryResults > options.Limits.MaxTopologyQueryResults) throw new CadImportException("Linework spatial queries exceed the safe topology budget.");
            foreach (var j in hits)
            {
                if (j >= i) continue;
                if (++candidates > options.Limits.MaxTopologyCandidates) throw new CadImportException("Linework spatial candidates exceed the safe topology budget.");
                var other = records[j];
                if (!current.Line.Intersects(other.Line)) continue;
                var intersection = current.Line.Intersection(other.Line);
                if (intersection.Length > minimumOverlap)
                    throw new CadImportException($"Duplicate/overlapping linework: {current.EntityType} {current.Handle} and {other.EntityType} {other.Handle} on '{current.Layer}'.");
                if (current.Line.Crosses(other.Line) && !IsNearSharedEndpoint(intersection, current.Line, other.Line, options.SnapTolerance))
                    throw new CadImportException($"Crossing linework on layer '{current.Layer}' would invent cut boundaries.");
            }
        }
    }

    private static bool IsNearSharedEndpoint(Geometry intersection, LineString first, LineString second, double tolerance)
    {
        if (intersection.IsEmpty) return false;
        var squaredTolerance = tolerance * tolerance;
        foreach (var point in intersection.Coordinates)
        {
            if (!IsNearEndpoint(first, point, squaredTolerance) || !IsNearEndpoint(second, point, squaredTolerance))
                return false;
        }
        return true;
    }

    private static bool IsNearEndpoint(LineString line, Coordinate point, double squaredTolerance)
    {
        var coordinates = line.Coordinates;
        return DistanceSquared(coordinates[0], point) <= squaredTolerance ||
            DistanceSquared(coordinates[^1], point) <= squaredTolerance;
    }

    private static double DistanceSquared(Coordinate first, Coordinate second)
    {
        var x = first.X - second.X; var y = first.Y - second.Y;
        return x * x + y * y;
    }

    private static IEnumerable<LineString> Lines(Geometry geometry)
    {
        if (geometry is LineString line) { yield return line; yield break; }
        if (geometry is GeometryCollection)
            for (var i = 0; i < geometry.NumGeometries; i++)
                foreach (var child in Lines(geometry.GetGeometryN(i))) yield return child;
    }

    private static int CountBranches(Geometry geometry, double tolerance, CancellationToken token)
    {
        var degree = new Dictionary<(double X, double Y), int>();
        var cells = new Dictionary<(long X, long Y), List<(double X, double Y)>>();
        (double X, double Y) Canonical(Coordinate p)
        {
            var cell = ((long)Math.Floor(p.X / tolerance), (long)Math.Floor(p.Y / tolerance));
            for (var x = -1; x <= 1; x++) for (var y = -1; y <= 1; y++)
                if (cells.TryGetValue((cell.Item1 + x, cell.Item2 + y), out var values))
                    foreach (var candidate in values)
                        if (Math.Pow(candidate.X - p.X, 2) + Math.Pow(candidate.Y - p.Y, 2) <= tolerance * tolerance) return candidate;
            if (!cells.TryGetValue(cell, out var bucket)) cells[cell] = bucket = [];
            bucket.Add((p.X, p.Y)); return (p.X, p.Y);
        }
        foreach (var line in Lines(geometry))
        {
            token.ThrowIfCancellationRequested(); var coordinates = line.Coordinates;
            for (var i = 1; i < coordinates.Length; i++)
            {
                var a = Canonical(coordinates[i - 1]); var b = Canonical(coordinates[i]);
                if (a == b) continue;
                degree[a] = degree.GetValueOrDefault(a) + 1; degree[b] = degree.GetValueOrDefault(b) + 1;
            }
        }
        return degree.Values.Count(d => d > 2);
    }

    private ImmutableArray<Contour> MarkSheetFrames(List<RingRecord> records, CancellationToken token)
    {
        var polygons = records.Select(r => r.Polygon).ToArray();
        var probes = polygons.Select(p => p.InteriorPoint).ToArray();
        var polygonTree = new STRtree<int>(); var probeTree = new STRtree<int>();
        for (var i = 0; i < polygons.Length; i++) { polygonTree.Insert(polygons[i].EnvelopeInternal, i); probeTree.Insert(probes[i].EnvelopeInternal, i); }
        polygonTree.Build(); probeTree.Build(); var result = ImmutableArray.CreateBuilder<Contour>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var polygon = polygons[i];
            var nested = polygonTree.Query(polygon.EnvelopeInternal).Any(j => j != i && polygons[j].Area > polygon.Area * (1 + 1e-10) && polygons[j].Covers(polygon));
            var contained = probeTree.Query(polygon.EnvelopeInternal).Where(j => j != i && polygon.Area > polygons[j].Area * (1 + 1e-10) && polygon.Covers(probes[j])).Take(2).Count();
            var envelope = polygon.EnvelopeInternal; var rectangularity = polygon.Area / (envelope.Width * envelope.Height);
            var contour = new Contour($"C{i + 1:0000}", records[i].Layer, polygon,
                !nested && contained >= 2 && rectangularity >= .95, records[i].Types);
            // Recognize each independent standard sheet, including one-part and
            // empty sheets beside a board already found by the heuristic.
            if (!nested && !contour.IsOuterCandidate && MatchesDefaultSheetSize(contour.Bounds) && ToolpathPlanner.IsSheetRectangle(contour))
                contour = new(contour.Id, contour.Layer, contour.Geometry, true, contour.SourceEntityTypes);
            result.Add(contour);
        }
        return result.MoveToImmutable();
    }

    private static bool MatchesDefaultSheetSize(Bounds2 bounds)
    {
        const double width = 1220, height = 2745, tolerance = .01;
        // Allow floating-point subtraction noise at translated CAD coordinates.
        static bool Matches(double actual, double expected) => Math.Abs(actual - expected) <= tolerance + 1e-8;
        return Matches(bounds.Width, width) && Matches(bounds.Height, height) ||
            Matches(bounds.Width, height) && Matches(bounds.Height, width);
    }

    private static double MillimetreScale(int unit) => unit switch
    {
        0 or 4 => 1, 1 => 25.4, 2 => 304.8, 3 => 1_609_344, 5 => 10, 6 => 1000, 7 => 1_000_000,
        8 => .0000254, 9 => .0254, 10 => 914.4, 11 => .0000001, 12 => .000001, 13 => .001,
        14 => 100, 15 => 10_000, 16 => 100_000, 17 => 1_000_000_000_000,
        18 => 149_597_870_700_000, 19 => 9.4607304725808e18, 20 => 3.0856775814913673e19,
        21 => 1_200_000.0 / 3937, 22 => 100_000.0 / 3937, 23 => 3_600_000.0 / 3937, 24 => 6_336_000_000.0 / 3937,
        _ => throw new CadImportException($"Unsupported CAD unit code {unit}.")
    };
}
