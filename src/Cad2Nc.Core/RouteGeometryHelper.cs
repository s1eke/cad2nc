using System.Collections.Immutable;
using System.Globalization;
using NetTopologySuite.Geometries;

namespace Cad2Nc.Core;

/// <summary>
/// Small, shared operations for the quantized closed routes emitted to NC.
/// Keeping these operations in Core makes planner safety-point decisions agree
/// with the coordinates and ramp end points that an NC writer will actually use.
/// </summary>
internal static class RouteGeometryHelper
{
    internal const double AxisResolution = 0.001;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal readonly record struct QuantizedRoute(
        ImmutableArray<Point2> Points,
        ImmutableArray<double> SegmentLengths)
    {
        internal double Perimeter => SegmentLengths.IsDefaultOrEmpty ? 0 : SegmentLengths.Sum();
    }

    internal readonly record struct RoutePosition(Point2 Point, int NextSegmentIndex);

    /// <summary>Rounds exactly as the NC axis formatter (including negative zero cleanup).</summary>
    internal static double Quantize(double value)
    {
        if (!double.IsFinite(value)) return value;
        if (Math.Abs(value) < AxisResolution / 2) return 0;
        return double.Parse(value.ToString("F3", Invariant), Invariant);
    }

    internal static Point2 Quantize(Point2 point) => new(Quantize(point.X), Quantize(point.Y));

    /// <summary>
    /// Quantizes an explicit closed route and rejects zero-length segments or a
    /// route which is no longer a valid positive-area polygon after rounding.
    /// The returned points retain the explicit closing point.  Keeping this
    /// validity check here makes planner candidate filtering agree with the
    /// NC writer's final preflight, which emits the same 0.001 mm coordinates.
    /// </summary>
    internal static bool TryQuantizeClosedRoute(IReadOnlyList<Point2> route, out QuantizedRoute quantized)
    {
        quantized = default;
        if (route is null || route.Count < 4 || route[0] != route[^1]) return false;
        var points = route.Select(Quantize).ToImmutableArray();
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)) || points[0] != points[^1]) return false;
        var lengths = ImmutableArray.CreateBuilder<double>(points.Length - 1);
        for (var index = 0; index < points.Length - 1; index++)
        {
            var length = Distance(points[index], points[index + 1]);
            if (!double.IsFinite(length) || length <= 0) return false;
            lengths.Add(length);
        }
        var result = new QuantizedRoute(points, lengths.ToImmutable());
        if (!double.IsFinite(result.Perimeter) || result.Perimeter <= 0) return false;
        // Quantization can introduce a crossing, collapse a lobe, or turn a
        // very small loop into a zero-area ring even when every individual
        // segment remains non-zero.  Such a route cannot be emitted safely.
        try
        {
            var ring = new LinearRing(points
                .Select(point => new Coordinate(point.X, point.Y)).ToArray());
            var polygon = new Polygon(ring);
            if (polygon.IsEmpty || polygon.Area <= 0 || !polygon.IsValid) return false;
        }
        catch (ArgumentException)
        {
            // NTS can reject malformed/overflowing rings at construction time.
            // Treat those the same as any other quantized candidate that cannot
            // be emitted; callers can then apply their documented fallback.
            return false;
        }
        catch (TopologyException)
        {
            // Robustness failures are data-level invalidity here, not a reason
            // to abort an otherwise independent candidate evaluation.
            return false;
        }
        quantized = result;
        return true;
    }

    /// <summary>
    /// Finds the point reached after travelling a distance along a quantized,
    /// repeatedly closed route. Distances beyond one lap wrap deterministically.
    /// </summary>
    internal static bool TryAdvanceAlongClosedRoute(IReadOnlyList<Point2> route, double distance,
        out RoutePosition position)
        => TryAdvanceAlongClosedRoute(route, distance, out position, out _);

    /// <summary>
    /// Finds the same endpoint as <see cref="TryAdvanceAlongClosedRoute(IReadOnlyList{Point2},double,out RoutePosition)"/>
    /// and reports whether the quantized ramp contains at least one effective XY
    /// move.  A positive geometric distance can still round to the entry point
    /// when it is shorter than the output coordinate quantum; callers must not
    /// claim such a ramp is usable.
    /// </summary>
    internal static bool TryAdvanceAlongClosedRoute(IReadOnlyList<Point2> route, double distance,
        out RoutePosition position, out bool hasEffectiveMovement)
    {
        position = default;
        hasEffectiveMovement = false;
        if (!double.IsFinite(distance) || distance < 0 ||
            !TryQuantizeClosedRoute(route, out var quantized)) return false;

        var perimeter = quantized.Perimeter;
        var remaining = distance % perimeter;
        var tolerance = Math.Max(1e-12, perimeter * 1e-12);
        // A complete lap necessarily emitted at least one non-zero segment,
        // even when the final endpoint wraps back to the entry point.
        hasEffectiveMovement = distance > tolerance && distance >= perimeter - tolerance;
        if (remaining <= tolerance || perimeter - remaining <= tolerance)
        {
            position = new(quantized.Points[0], 0);
            return true;
        }

        for (var index = 0; index < quantized.SegmentLengths.Length; index++)
        {
            var length = quantized.SegmentLengths[index];
            if (remaining <= length + tolerance)
            {
                var fraction = Math.Clamp(remaining / length, 0, 1);
                var a = quantized.Points[index];
                var b = quantized.Points[index + 1];
                var point = fraction >= 1 - tolerance
                    ? b
                    : Quantize(new Point2(a.X + (b.X - a.X) * fraction,
                        a.Y + (b.Y - a.Y) * fraction));
                position = new(point, (index + 1) % quantized.SegmentLengths.Length);
                hasEffectiveMovement |= point != quantized.Points[0];
                return true;
            }
            remaining -= length;
        }

        // Floating-point accumulation can leave a residual smaller than the
        // final tolerance. In that case the mathematically closed endpoint is
        // the start point, which is also what the NC writer emits.
        position = new(quantized.Points[0], 0);
        return true;
    }

    internal static double Distance(Point2 a, Point2 b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
