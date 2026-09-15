using System.Collections.Immutable;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

/// <summary>Small, display-only geometry; never used for planning or hit testing.</summary>
public static class ContourThumbnailGeometry
{
    public const double Size = 40;
    public const int MaximumPointCount = 256;
    private const double Padding = 5;

    public static ImmutableArray<Point2> Create(Contour contour)
    {
        ArgumentNullException.ThrowIfNull(contour);
        var bounds = contour.Bounds;
        var extent = Math.Max(bounds.Width, bounds.Height);
        if (!double.IsFinite(extent) || extent <= 0) return [];
        var scale = (Size - Padding * 2) / extent;
        var offsetX = (Size - bounds.Width * scale) / 2;
        var offsetY = (Size - bounds.Height * scale) / 2;
        // Subtract the CAD reference before scaling to retain small details at large coordinates.
        var points = contour.Points.Select(point => new Point2(
            offsetX + (point.X - bounds.MinX) * scale,
            offsetY + (bounds.MaxY - point.Y) * scale)).ToArray();
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return [];
        if (points.Length <= MaximumPointCount) return [.. points];

        // Protect the bounding extrema and the closing point before simplifying. An index-based
        // sample can lose an entire side when, for example, only one rectangle edge is densely
        // tessellated. Work in thumbnail pixels and retain the largest remaining deviation first.
        var kept = new SortedSet<int> { 0, points.Length - 1 };
        var minX = 0; var maxX = 0; var minY = 0; var maxY = 0;
        for (var index = 1; index < points.Length - 1; index++)
        {
            if (points[index].X < points[minX].X) minX = index;
            if (points[index].X > points[maxX].X) maxX = index;
            if (points[index].Y < points[minY].Y) minY = index;
            if (points[index].Y > points[maxY].Y) maxY = index;
        }
        kept.UnionWith([minX, maxX, minY, maxY]);
        var segments = new PriorityQueue<(int Start, int End, int Split), double>();
        var anchors = kept.ToArray();
        for (var index = 1; index < anchors.Length; index++) QueueSegment(anchors[index - 1], anchors[index]);
        while (kept.Count < MaximumPointCount && segments.TryDequeue(out var segment, out _))
        {
            kept.Add(segment.Split);
            QueueSegment(segment.Start, segment.Split);
            QueueSegment(segment.Split, segment.End);
        }
        return [.. kept.Select(index => points[index])];

        void QueueSegment(int start, int end)
        {
            var a = points[start]; var b = points[end];
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var lengthSquared = dx * dx + dy * dy;
            const double pixelToleranceSquared = .2 * .2;
            var largestDistance = pixelToleranceSquared;
            var split = -1;
            for (var index = start + 1; index < end; index++)
            {
                var point = points[index];
                var t = lengthSquared == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
                var distanceX = point.X - a.X - t * dx;
                var distanceY = point.Y - a.Y - t * dy;
                var distance = distanceX * distanceX + distanceY * distanceY;
                if (distance <= largestDistance) continue;
                largestDistance = distance; split = index;
            }
            if (split >= 0) segments.Enqueue((start, end, split), -largestDistance);
        }
    }
}
