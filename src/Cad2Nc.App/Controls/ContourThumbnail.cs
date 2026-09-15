using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Cad2Nc.App.Controls;

/// <summary>A virtualized-list friendly vector thumbnail with no per-row GPU surface.</summary>
public sealed class ContourThumbnail : UserControl
{
    private sealed record CachedPoints(ImmutableArray<Point2> Points);
    private static readonly ConditionalWeakTable<Contour, CachedPoints> Cache = new();
    private readonly ShapePath _shape = new()
    {
        StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.None,
        IsHitTestVisible = false
    };

    public static readonly DependencyProperty ContourProperty = DependencyProperty.Register(
        nameof(Contour), typeof(Contour), typeof(ContourThumbnail),
        new PropertyMetadata(null, (sender, _) => ((ContourThumbnail)sender).UpdateContour()));

    public Contour? Contour
    {
        get => (Contour?)GetValue(ContourProperty);
        set => SetValue(ContourProperty, value);
    }

    public ContourThumbnail()
    {
        Width = Height = ContourThumbnailGeometry.Size;
        IsTabStop = false;
        IsHitTestVisible = false;
        var background = new Border { CornerRadius = new CornerRadius(7), Child = _shape };
        Content = background;
        void ApplyPalette()
        {
            var dark = ActualTheme == ElementTheme.Dark;
            background.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 42, 54, 67) : Color.FromArgb(255, 237, 242, 246));
            _shape.Stroke = new SolidColorBrush(dark ? Color.FromArgb(255, 163, 198, 220) : Color.FromArgb(255, 61, 99, 128));
        }
        ActualThemeChanged += (_, _) => ApplyPalette();
        ApplyPalette();
    }

    private void UpdateContour()
    {
        if (Contour is not { } contour) { _shape.Data = null; return; }
        var points = Cache.GetValue(contour, static key => new CachedPoints(ContourThumbnailGeometry.Create(key))).Points;
        AutomationProperties.SetName(this, $"轮廓 {contour.Id} 的形状");
        if (points.Length < 2) { _shape.Data = null; return; }
        var figure = new PathFigure { StartPoint = new Point(points[0].X, points[0].Y), IsClosed = true, IsFilled = false };
        var segment = new PolyLineSegment();
        for (var index = 1; index < points.Length - 1; index++)
            segment.Points.Add(new Point(points[index].X, points[index].Y));
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _shape.Data = geometry;
    }
}
