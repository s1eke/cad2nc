using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Core;

internal readonly record struct CadVector(double X, double Y, double Z)
{
    public static CadVector From(XYZ p) => new(p.X, p.Y, p.Z);
    public static CadVector operator +(CadVector a, CadVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CadVector operator -(CadVector a, CadVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static CadVector operator *(CadVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public CadVector Normalized() => Length > 1e-15 && double.IsFinite(Length)
        ? this * (1 / Length) : throw new CadImportException("CAD normal is zero or invalid.");
    public static CadVector Cross(CadVector a, CadVector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>Double precision affine transform: columns are transformed basis vectors.</summary>
internal readonly record struct CadTransform(CadVector X, CadVector Y, CadVector Z, CadVector Origin)
{
    public static CadTransform Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), default);
    public CadVector Vector(CadVector p) => X * p.X + Y * p.Y + Z * p.Z;
    public CadVector Point(CadVector p) => Vector(p) + Origin;
    public CadTransform Then(CadTransform child) => new(Vector(child.X), Vector(child.Y), Vector(child.Z), Point(child.Origin));
    public static CadTransform Scale(double x, double y, double z) => new(new(x, 0, 0), new(0, y, 0), new(0, 0, z), default);
    public static CadTransform Translation(CadVector p) => Identity with { Origin = p };
    public static CadTransform Rotation(double angle) => new(new(Math.Cos(angle), Math.Sin(angle), 0), new(-Math.Sin(angle), Math.Cos(angle), 0), new(0, 0, 1), default);
    public static CadTransform Ocs(XYZ normal)
    {
        var z = CadVector.From(normal).Normalized();
        // AutoCAD arbitrary-axis algorithm (the 1/64 threshold is part of the format).
        var x = CadVector.Cross(Math.Abs(z.X) < 1.0 / 64 && Math.Abs(z.Y) < 1.0 / 64
            ? new(0, 1, 0) : new(0, 0, 1), z).Normalized();
        return new(x, CadVector.Cross(z, x), z, default);
    }
}

internal sealed record CadLineRecord(string Layer, string EntityType, string Handle, LineString Line, bool IsCircle = false);

/// <summary>
/// Interprets source primitives without ACadSharp's Explode/PolygonalVertexes helpers.
/// Tolerances are evaluated after every block/OCS/unit transform, in millimetres.
/// </summary>
internal sealed class CadEntityFlattener
{
    // The public tolerance is a maximum, not a requirement to spend the entire budget.
    // Finer machining curves keep chord-corner artifacts compatible with the 0.001 mm
    // NC grid and conservative cutter-envelope/narrow-feature checks.
    private const double CurvePrecisionCap = 0.01;
    private readonly ImportOptions options;
    private readonly CancellationToken cancellationToken;
    private readonly GeometryFactory factory = new();
    private readonly List<CadLineRecord> lines = [];
    private readonly Dictionary<string, int> ignored = new(StringComparer.Ordinal);
    private int visitedEntities, vertexCount, closedCount;
    private double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
    public IReadOnlyDictionary<string, int> Ignored => ignored;
    public double Elevation => double.IsFinite(minZ) ? (minZ + maxZ) * .5 : 0;

    public CadEntityFlattener(ImportOptions options, CancellationToken cancellationToken)
    {
        this.options = options;
        this.cancellationToken = cancellationToken;
    }

    public List<CadLineRecord> Flatten(ACadSharp.CadDocument document, double scale)
    {
        var transform = CadTransform.Scale(scale, scale, scale);
        var blocks = new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);
        foreach (var entity in document.Entities) Expand(entity, transform, "0", blocks, 0);
        if (maxZ - minZ > ZTolerance)
            throw new CadImportException("Machining entities lie on multiple Z planes; partial NC output is not allowed.");
        return lines;
    }

    private double ZTolerance => Math.Max(1e-6, Math.Max(options.FlatteningTolerance, options.SnapTolerance));
    private double CurveTolerance => Math.Min(options.FlatteningTolerance, CurvePrecisionCap);

    private void Expand(Entity entity, CadTransform transform, string inheritedLayer, HashSet<BlockRecord> visiting, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++visitedEntities > options.Limits.MaxMachiningEntities)
            throw new CadImportException("Drawing exceeds the machining entities resource limit.");
        var layer = entity.Layer?.Name ?? "0";
        if (layer == "0") layer = inheritedLayer;
        if (entity is Insert insert)
        {
            var block = insert.Block;
            if (block is null || ((int)block.BlockEntity.Flags & 12) != 0 || block.IsUnloaded ||
                !string.IsNullOrWhiteSpace(block.BlockEntity.XRefPath))
                throw new CadImportException($"Unresolved external-reference INSERT {insert.Handle:X}.");
            if (depth >= options.Limits.MaxInsertDepth || !visiting.Add(block))
                throw new CadImportException($"Cyclic or excessively nested INSERT {insert.Handle:X}.");
            if (new[] { insert.XScale, insert.YScale, insert.ZScale }.Any(v => !double.IsFinite(v) || v == 0))
                throw new CadImportException($"Invalid scale in INSERT {insert.Handle:X}.");
            var rows = Math.Max(1, (int)insert.RowCount);
            var columns = Math.Max(1, (int)insert.ColumnCount);
            if ((long)rows * columns > options.Limits.MaxMachiningEntities)
                throw new CadImportException("INSERT array exceeds the machining entities resource limit.");
            var placement = transform.Then(CadTransform.Ocs(insert.Normal))
                .Then(CadTransform.Translation(CadVector.From(insert.InsertPoint)))
                .Then(CadTransform.Rotation(insert.Rotation));
            for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                // Array spacing is rotated but is not multiplied by the insertion scale.
                var cell = placement.Then(CadTransform.Translation(new(column * insert.ColumnSpacing, row * insert.RowSpacing, 0)))
                    .Then(CadTransform.Scale(insert.XScale, insert.YScale, insert.ZScale))
                    .Then(CadTransform.Translation(CadVector.From(block.BlockEntity.BasePoint) * -1));
                foreach (var child in block.Entities) Expand(child, cell, layer, visiting, depth + 1);
            }
            visiting.Remove(block);
            return;
        }

        List<CadVector>? points;
        var closed = false;
        var isCircle = false;
        switch (entity)
        {
            case Line line:
                points = [transform.Point(CadVector.From(line.StartPoint)), transform.Point(CadVector.From(line.EndPoint))];
                break;
            case Arc arc:
                points = CirclePoints(arc, transform, arc.StartAngle, PositiveSweep(arc.StartAngle, arc.EndAngle));
                break;
            case Circle circle:
                points = CirclePoints(circle, transform, 0, Math.Tau); closed = true;
                var circleTransform = transform.Then(CadTransform.Ocs(circle.Normal));
                var u = circleTransform.Vector(new(1, 0, 0));
                var v = circleTransform.Vector(new(0, 1, 0));
                var uSquared = u.X * u.X + u.Y * u.Y;
                var vSquared = v.X * v.X + v.Y * v.Y;
                var tolerance = Math.Max(uSquared, vSquared) * 1e-10;
                // Nonuniform block scaling can turn a source CIRCLE into an ellipse.
                isCircle = uSquared > 0 && Math.Abs(uSquared - vSquared) <= tolerance &&
                    Math.Abs(u.X * v.X + u.Y * v.Y) <= tolerance;
                break;
            case Ellipse ellipse:
                var major = CadVector.From(ellipse.MajorAxisEndPoint);
                var minor = CadVector.Cross(CadVector.From(ellipse.Normal).Normalized(), major) * ellipse.RadiusRatio;
                if (!(major.Length > 0) || !(ellipse.RadiusRatio > 0) || !double.IsFinite(ellipse.RadiusRatio))
                    throw new CadImportException($"Invalid ELLIPSE {entity.Handle:X}.");
                closed = ellipse.IsFullEllipse;
                points = ParametricArc(transform.Point(CadVector.From(ellipse.Center)), transform.Vector(major), transform.Vector(minor),
                    ellipse.StartParameter, closed ? Math.Tau : PositiveSweep(ellipse.StartParameter, ellipse.EndParameter));
                break;
            case LwPolyline polyline:
                CheckControlCount(polyline.Vertices.Count);
                closed = polyline.IsClosed;
                points = PolylinePoints(polyline.Vertices.Select(v => (new CadVector(v.Location.X, v.Location.Y, polyline.Elevation), v.Bulge)).ToArray(),
                    closed, transform.Then(CadTransform.Ocs(polyline.Normal)));
                break;
            case Polyline2D polyline:
                CheckControlCount(polyline.Vertices.Count);
                closed = polyline.IsClosed;
                // Legacy DXF fitted POLYLINE records already contain the stored vertex/bulge
                // path. Preserve that representation instead of refitting the vertices.
                points = PolylinePoints(polyline.Vertices.Select(v => (new CadVector(v.Location.X, v.Location.Y, polyline.Elevation), v.Bulge)).ToArray(),
                    closed, transform.Then(CadTransform.Ocs(polyline.Normal)));
                break;
            case Polyline3D polyline:
                CheckControlCount(polyline.Vertices.Count);
                closed = polyline.IsClosed;
                points = polyline.Vertices.Select(v => transform.Point(CadVector.From(v.Location))).ToList();
                break;
            case Spline spline:
                points = SplinePoints(spline, transform);
                closed = spline.IsClosed || (points.Count > 2 && (points[0] - points[^1]).Length < 1e-9);
                if (closed && points.Count > 1 && (points[0] - points[^1]).Length > CurveTolerance)
                    throw new CadImportException($"Closed SPLINE {entity.Handle:X} has inconsistent endpoints.");
                break;
            default:
                var type = entity.ObjectName;
                // Unknown/proxy entities may contain a machining boundary; never silently drop them.
                if (type.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || type.Contains("PROXY", StringComparison.OrdinalIgnoreCase))
                    throw new CadImportException($"Unsupported machining entity {type} {entity.Handle:X}; source geometry must be resolved before import.");
                ignored[type] = ignored.GetValueOrDefault(type) + 1;
                return;
        }
        AddLine(points, closed, layer, entity.ObjectName, entity.Handle, isCircle);
    }

    private void CheckControlCount(int count)
    {
        if (count > options.Limits.MaxEntityVertices)
            throw new CadImportException("CAD entity exceeds the control vertex resource limit.");
    }

    private void AddLine(List<CadVector> points, bool closed, string layer, string type, ulong handle, bool isCircle)
    {
        if (points.Count < 2) throw new CadImportException($"{type} {handle:X} produced no path.");
        ValidateBounds(points);
        var zlo = points.Min(p => p.Z); var zhi = points.Max(p => p.Z);
        if (zhi - zlo > ZTolerance) throw new CadImportException($"Non-planar {type} {handle:X} cannot be projected to XY.");
        minZ = Math.Min(minZ, zlo); maxZ = Math.Max(maxZ, zhi);
        var coordinates = new List<Coordinate>(points.Count + 1);
        foreach (var p in points)
        {
            if (coordinates.Count == 0 || Math.Abs(coordinates[^1].X - p.X) > 1e-10 || Math.Abs(coordinates[^1].Y - p.Y) > 1e-10)
                coordinates.Add(new Coordinate(p.X, p.Y));
        }
        if (closed && coordinates.Count > 2 && !coordinates[0].Equals2D(coordinates[^1])) coordinates.Add(coordinates[0].Copy());
        if (coordinates.Count < 2) throw new CadImportException($"Degenerate {type} {handle:X} produced no path.");
        if (closed && ++closedCount > options.Limits.MaxClosedContours) throw new CadImportException("Drawing exceeds the closed contours resource limit.");
        ReserveVertices(coordinates.Count);
        var geometry = factory.CreateLineString(coordinates.ToArray());
        if (!geometry.IsSimple) throw new CadImportException($"Self-intersecting {type} {handle:X} on layer '{layer}'.");
        if (geometry.Length > 1e-9) lines.Add(new(layer, type, handle.ToString("X"), geometry, isCircle));
    }

    private void ReserveVertices(int count)
    {
        if (count > options.Limits.MaxEntityVertices || (long)vertexCount + count > options.Limits.MaxDocumentVertices)
            throw new CadImportException("Drawing exceeds the flattened vertices resource limit.");
        vertexCount += count;
    }

    private void CheckPendingVertices(int count)
    {
        if (count > options.Limits.MaxEntityVertices || (long)vertexCount + count > options.Limits.MaxDocumentVertices)
            throw new CadImportException("Drawing exceeds the flattened vertices resource limit.");
    }

    private void ValidateBounds(IEnumerable<CadVector> points)
    {
        var loX = double.PositiveInfinity; var loY = double.PositiveInfinity;
        var hiX = double.NegativeInfinity; var hiY = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) ||
                Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > options.Limits.MaxAbsoluteCoordinate)
                throw new CadImportException("Entity exceeds supported CAD physical bounds.");
            loX = Math.Min(loX, p.X); hiX = Math.Max(hiX, p.X);
            loY = Math.Min(loY, p.Y); hiY = Math.Max(hiY, p.Y);
        }
        if (hiX - loX > options.Limits.MaxSpan || hiY - loY > options.Limits.MaxSpan)
            throw new CadImportException("Entity exceeds supported CAD physical bounds.");
    }

    private static double PositiveSweep(double start, double end)
    {
        var sweep = (end - start) % Math.Tau;
        return sweep <= 0 ? sweep + Math.Tau : sweep;
    }

    private List<CadVector> CirclePoints(Circle circle, CadTransform transform, double start, double sweep)
    {
        if (!(circle.Radius > 0) || !double.IsFinite(circle.Radius)) throw new CadImportException("Invalid circle/arc radius.");
        var ocs = transform.Then(CadTransform.Ocs(circle.Normal));
        return ParametricArc(ocs.Point(CadVector.From(circle.Center)), ocs.Vector(new(circle.Radius, 0, 0)),
            ocs.Vector(new(0, circle.Radius, 0)), start, sweep);
    }

    private List<CadVector> ParametricArc(CadVector center, CadVector u, CadVector v, double start, double sweep)
    {
        if (!double.IsFinite(start) || !double.IsFinite(sweep)) throw new CadImportException("Invalid curve parameters.");
        CadVector Evaluate(double angle) => center + u * Math.Cos(angle) + v * Math.Sin(angle);
        // Analytic extrema are checked before any tessellation allocation, including Z extrema.
        var extrema = new List<CadVector> { Evaluate(start), Evaluate(start + sweep) };
        var extremaFractions = new List<double>();
        foreach (var angle in new[] { Math.Atan2(v.X, u.X), Math.Atan2(v.Y, u.Y), Math.Atan2(v.Z, u.Z) })
        foreach (var candidate in new[] { angle, angle + Math.PI })
        {
            var delta = (candidate - start) % Math.Tau; if (delta < 0) delta += Math.Tau;
            if (Math.Abs(sweep) >= Math.Tau - 1e-12 || (sweep >= 0 ? delta <= sweep : (delta == 0 || delta - Math.Tau >= sweep)))
            {
                extrema.Add(Evaluate(candidate));
                var distance = sweep >= 0 ? delta : delta == 0 ? 0 : delta - Math.Tau;
                extremaFractions.Add(distance / sweep);
            }
        }
        ValidateBounds(extrema);
        if (extrema.Max(p => p.Z) - extrema.Min(p => p.Z) > ZTolerance)
            throw new CadImportException("Non-planar curve cannot be projected to XY.");
        var radiusBound = Math.Sqrt(u.Length * u.Length + v.Length * v.Length);
        var step = Math.Min(Math.PI / 2, 2 * Math.Acos(Math.Clamp(1 - CurveTolerance / Math.Max(radiusBound, 1e-300), -1, 1)));
        if (!(step > 0)) throw new CadImportException("Curve requires too many flattened vertices.");
        var required = Math.Ceiling(Math.Abs(sweep) / step);
        if (!double.IsFinite(required) || required + 1 > options.Limits.MaxEntityVertices)
            throw new CadImportException("Curve exceeds the flattened vertices resource limit.");
        var segments = Math.Max(1, (int)required);
        CheckPendingVertices(segments + 1 + extremaFractions.Count);
        var parameters = Enumerable.Range(0, segments + 1).Select(i => (double)i / segments)
            .Concat(extremaFractions).Where(t => t >= 0 && t <= 1).Distinct().Order().ToArray();
        var result = new List<CadVector>(parameters.Length);
        foreach (var parameter in parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Evaluate(start + sweep * parameter));
        }
        if (Math.Abs(Math.Abs(sweep) - Math.Tau) < 1e-12) result[^1] = result[0];
        return result;
    }

    private List<CadVector> PolylinePoints((CadVector Point, double Bulge)[] vertices, bool closed, CadTransform transform)
    {
        if (vertices.Length < 2) throw new CadImportException("POLYLINE produced no path.");
        ValidateBounds(vertices.Select(v => transform.Point(v.Point)));
        var result = new List<CadVector> { transform.Point(vertices[0].Point) };
        var segmentCount = closed ? vertices.Length : vertices.Length - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var (a, bulge) = vertices[i]; var b = vertices[(i + 1) % vertices.Length].Point;
            if (!double.IsFinite(bulge)) throw new CadImportException("Invalid POLYLINE bulge.");
            if (Math.Abs(bulge) < 1e-15) result.Add(transform.Point(b));
            else
            {
                var chord = b - a; var length = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
                if (length <= 1e-12) throw new CadImportException("Non-zero POLYLINE bulge has coincident endpoints.");
                var center = (a + b) * .5 + new CadVector(-chord.Y, chord.X, 0) * ((1 - bulge * bulge) / (4 * bulge));
                var radius = length * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
                var start = Math.Atan2(a.Y - center.Y, a.X - center.X);
                var arc = ParametricArc(transform.Point(center), transform.Vector(new(radius, 0, 0)), transform.Vector(new(0, radius, 0)), start, 4 * Math.Atan(bulge));
                CheckPendingVertices(result.Count + arc.Count - 1);
                result.AddRange(arc.Skip(1)); result[^1] = transform.Point(b);
            }
            CheckPendingVertices(result.Count);
        }
        return result;
    }

    private readonly record struct Homogeneous(CadVector A, double W)
    {
        public static Homogeneous operator +(Homogeneous a, Homogeneous b) => new(a.A + b.A, a.W + b.W);
        public static Homogeneous operator -(Homogeneous a, Homogeneous b) => new(a.A - b.A, a.W - b.W);
        public static Homogeneous operator *(Homogeneous a, double s) => new(a.A * s, a.W * s);
    }

    private List<CadVector> SplinePoints(Spline spline, CadTransform transform)
    {
        if (spline.ControlPoints.Count == 0 && spline.FitPoints.Count > 0)
            spline = InterpolateFitSpline(spline, transform);
        if (spline.Knots.Count == 0 && spline.Degree >= 1 && spline.Degree <= 32 && spline.ControlPoints.Count > spline.Degree)
        {
            CheckControlCount(spline.ControlPoints.Count);
            spline = (Spline)spline.Clone();
            var intervals = spline.ControlPoints.Count - spline.Degree;
            spline.Knots.AddRange(Enumerable.Repeat(0.0, spline.Degree + 1));
            for (var i = 1; i < intervals; i++) spline.Knots.Add((double)i / intervals);
            spline.Knots.AddRange(Enumerable.Repeat(1.0, spline.Degree + 1));
        }
        var count = spline.ControlPoints.Count; var degree = spline.Degree; var knots = spline.Knots;
        CheckControlCount(count);
        if (degree < 1 || degree > 32 || count <= degree || knots.Count != count + degree + 1)
            throw new CadImportException("SPLINE has invalid degree, knots, or control points.");
        if (knots.Any(v => !double.IsFinite(v)) || knots.Zip(knots.Skip(1)).Any(p => p.First > p.Second))
            throw new CadImportException("SPLINE knots are invalid.");
        if (spline.Weights.Count != 0 && spline.Weights.Count != count) throw new CadImportException("SPLINE weight count is invalid.");
        var weights = Enumerable.Range(0, count).Select(i => spline.Weights.Count == 0 ? 1.0 : spline.Weights[i]).ToArray();
        if (weights.Any(w => !double.IsFinite(w) || w <= 0)) throw new CadImportException("SPLINE requires finite positive rational weights.");
        var controls = spline.ControlPoints.Select(p => transform.Point(CadVector.From(p))).ToArray();
        ValidateBounds(controls);
        var result = new List<CadVector>();
        for (var span = degree; span < count; span++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = knots[span]; var right = knots[span + 1]; if (right <= left) continue;
            var origin = controls[span - degree];
            var h = Enumerable.Range(span - degree, degree + 1).Select(i => new Homogeneous((controls[i] - origin) * weights[i], weights[i])).ToArray();
            var d1 = new Homogeneous[degree];
            for (var j = 0; j < degree; j++)
            {
                var i = span - degree + j; var denominator = knots[i + degree + 1] - knots[i + 1];
                d1[j] = denominator > 0 ? (h[j + 1] - h[j]) * (degree / denominator) : default;
            }
            var d2 = new Homogeneous[Math.Max(0, degree - 1)];
            for (var j = 0; j < degree - 1; j++)
            {
                var i = span - degree + j; var denominator = knots[i + degree + 1] - knots[i + 2];
                d2[j] = denominator > 0 ? (d1[j + 1] - d1[j]) * ((degree - 1) / denominator) : default;
            }
            var wmin = h.Min(p => p.W); var a0 = h.Max(p => p.A.Length);
            var a1 = d1.Max(p => p.A.Length); var w1 = d1.Max(p => Math.Abs(p.W));
            var a2 = d2.Length == 0 ? 0 : d2.Max(p => p.A.Length); var w2 = d2.Length == 0 ? 0 : d2.Max(p => Math.Abs(p.W));
            // Convex-hull derivative bound: chord error <= max|C''| * dt² / 8.
            // This cannot miss a narrow loop between samples, unlike midpoint-only flattening.
            var secondDerivativeBound = a2 / wmin + a0 * w2 / (wmin * wmin) +
                2 * a1 * w1 / (wmin * wmin) + 2 * a0 * w1 * w1 / (wmin * wmin * wmin);
            var required = Math.Ceiling((right - left) * Math.Sqrt(secondDerivativeBound / (8 * CurveTolerance)));
            if (!double.IsFinite(required) || required > options.Limits.MaxEntityVertices)
                throw new CadImportException("SPLINE exceeds the adaptive flattened vertices resource limit.");
            var segments = Math.Max(1, (int)required);
            CheckPendingVertices(result.Count + segments + 1);
            for (var step = 0; step <= segments; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = left + (right - left) * step / segments;
                var work = (Homogeneous[])h.Clone();
                for (var r = 1; r <= degree; r++)
                for (var j = degree; j >= r; j--)
                {
                    var i = span - degree + j;
                    var denominator = knots[i + degree - r + 1] - knots[i];
                    var alpha = denominator == 0 ? 0 : (t - knots[i]) / denominator;
                    work[j] = work[j - 1] * (1 - alpha) + work[j] * alpha;
                }
                var value = work[degree];
                if (!(value.W > 0)) throw new CadImportException("SPLINE evaluation produced an invalid weight.");
                var point = value.A * (1 / value.W) + origin;
                if (step == 0 && result.Count > 0)
                {
                    if ((point - result[^1]).Length > CurveTolerance)
                        throw new CadImportException("SPLINE has a discontinuous knot span; joining it would invent a machining segment.");
                    continue;
                }
                result.Add(point);
            }
        }
        if (result.Count < 2) throw new CadImportException("SPLINE produced no path.");
        return result;
    }

    private Spline InterpolateFitSpline(Spline source, CadTransform transform)
    {
        var count = source.FitPoints.Count;
        if (count < 2) throw new CadImportException("SPLINE requires at least two distinct fit points.");
        if ((long)(count - 1) * 3 + 1 > options.Limits.MaxEntityVertices)
            throw new CadImportException("Fit-point SPLINE exceeds the control vertex resource limit.");
        var points = source.FitPoints.Select(CadVector.From).ToArray();
        ValidateBounds(points.Select(transform.Point));
        var widths = new double[count - 1]; var total = 0.0;
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] = (points[i + 1] - points[i]).Length;
            if (!double.IsFinite(widths[i]) || widths[i] <= 1e-12)
                throw new CadImportException("SPLINE has coincident or invalid consecutive fit points.");
            total += widths[i];
        }
        for (var i = 0; i < widths.Length; i++) widths[i] /= total;
        var lower = new double[count]; var diagonal = new double[count]; var upper = new double[count];
        var right = new CadVector[count];
        // Fit-only DXF splines use chord-length parameterization and a cubic interpolant.
        // When both endpoint tangent directions exist, constrain them using the
        // total-chord-length magnitude rule.
        var start = CadVector.From(source.StartTangent); var end = CadVector.From(source.EndTangent);
        if (new[] { start.X, start.Y, start.Z, end.X, end.Y, end.Z }.Any(v => !double.IsFinite(v)))
            throw new CadImportException("SPLINE endpoint tangent is invalid.");
        var constrained = start.Length > 1e-12 && end.Length > 1e-12;
        if (constrained)
        {
            start = start.Normalized() * total; end = end.Normalized() * total;
            diagonal[0] = 2 * widths[0]; upper[0] = widths[0];
            right[0] = ((points[1] - points[0]) * (1 / widths[0]) - start) * 6;
            lower[^1] = widths[^1]; diagonal[^1] = 2 * widths[^1];
            right[^1] = (end - (points[^1] - points[^2]) * (1 / widths[^1])) * 6;
        }
        else { diagonal[0] = 1; diagonal[^1] = 1; }
        for (var i = 1; i < count - 1; i++)
        {
            lower[i] = widths[i - 1]; diagonal[i] = 2 * (widths[i - 1] + widths[i]); upper[i] = widths[i];
            right[i] = ((points[i + 1] - points[i]) * (1 / widths[i]) - (points[i] - points[i - 1]) * (1 / widths[i - 1])) * 6;
        }
        // Thomas solve: linear time and memory, unlike a dense global interpolation matrix.
        for (var i = 1; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ratio = lower[i] / diagonal[i - 1]; diagonal[i] -= ratio * upper[i - 1]; right[i] -= right[i - 1] * ratio;
            if (!(diagonal[i] > 0) || !double.IsFinite(diagonal[i])) throw new CadImportException("Fit-point SPLINE interpolation is singular.");
        }
        var second = new CadVector[count]; second[^1] = right[^1] * (1 / diagonal[^1]);
        for (var i = count - 2; i >= 0; i--) second[i] = (right[i] - second[i + 1] * upper[i]) * (1 / diagonal[i]);
        var result = new Spline { Degree = 3, Flags = source.Flags & ~SplineFlags.Rational };
        static XYZ ToXyz(CadVector p) => new(p.X, p.Y, p.Z);
        result.ControlPoints.Add(ToXyz(points[0])); result.Knots.AddRange(new double[] { 0, 0, 0, 0 });
        var parameter = 0.0;
        for (var i = 0; i < count - 1; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var h = widths[i]; var chordDerivative = (points[i + 1] - points[i]) * (1 / h);
            var initialDerivative = chordDerivative - (second[i] * 2 + second[i + 1]) * (h / 6);
            var finalDerivative = chordDerivative + (second[i] + second[i + 1] * 2) * (h / 6);
            result.ControlPoints.Add(ToXyz(points[i] + initialDerivative * (h / 3)));
            result.ControlPoints.Add(ToXyz(points[i + 1] - finalDerivative * (h / 3)));
            result.ControlPoints.Add(ToXyz(points[i + 1]));
            parameter += h;
            if (i < count - 2) result.Knots.AddRange(new[] { parameter, parameter, parameter });
        }
        result.Knots.AddRange(new double[] { 1, 1, 1, 1 });
        return result;
    }
}
