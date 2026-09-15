using Cad2Nc.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Cad2Nc.App.Controls;

/// <summary>Symbolic retained-material and tool-center relationship, not a contour preview.</summary>
public sealed class MachiningSideDiagram : UserControl
{
    private readonly Canvas _drawing = new() { Width = 128, Height = 78, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _description = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(
        nameof(Side), typeof(CutSide), typeof(MachiningSideDiagram),
        new PropertyMetadata(CutSide.Outside, (sender, _) => ((MachiningSideDiagram)sender).DrawDiagram()));

    public CutSide Side
    {
        get => (CutSide)GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    public MachiningSideDiagram()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(_drawing);
        stack.Children.Add(_description);
        stack.Children.Add(new TextBlock { Text = "加工侧示意 · 非实际轮廓", FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
        Content = stack;
        ActualThemeChanged += (_, _) => DrawDiagram();
        DrawDiagram();
    }

    private void DrawDiagram()
    {
        _drawing.Children.Clear();
        var dark = ActualTheme == ElementTheme.Dark;
        var material = new SolidColorBrush(dark ? Color.FromArgb(255, 47, 72, 64) : Color.FromArgb(255, 219, 239, 229));
        var edge = new SolidColorBrush(dark ? Color.FromArgb(255, 131, 174, 155) : Color.FromArgb(255, 86, 135, 112));
        var tool = new SolidColorBrush(dark ? Color.FromArgb(255, 102, 193, 247) : Color.FromArgb(255, 36, 128, 189));
        var isInside = Side == CutSide.Inside;
        if (isInside)
        {
            var region = new GeometryGroup { FillRule = FillRule.EvenOdd };
            region.Children.Add(new RectangleGeometry { Rect = new Rect(8, 7, 112, 64) });
            region.Children.Add(new EllipseGeometry { Center = new Point(64, 39), RadiusX = 25, RadiusY = 25 });
            _drawing.Children.Add(new ShapePath { Data = region, Fill = material });
        }
        else
        {
            var retained = new Ellipse { Width = 50, Height = 50, Fill = material };
            Canvas.SetLeft(retained, 39); Canvas.SetTop(retained, 14);
            _drawing.Children.Add(retained);
        }
        var contour = new Ellipse { Width = 50, Height = 50, Stroke = edge, StrokeThickness = 1.5 };
        Canvas.SetLeft(contour, 39); Canvas.SetTop(contour, 14); _drawing.Children.Add(contour);
        var routeRadius = isInside ? 17d : 33d;
        var route = new Ellipse { Width = routeRadius * 2, Height = routeRadius * 2, Stroke = tool, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 3, 3 } };
        Canvas.SetLeft(route, 64 - routeRadius); Canvas.SetTop(route, 39 - routeRadius); _drawing.Children.Add(route);
        var cutter = new Ellipse { Width = 16, Height = 16, Stroke = tool, StrokeThickness = 1.7, Fill = new SolidColorBrush(Color.FromArgb(50, 58, 156, 218)) };
        Canvas.SetLeft(cutter, 64 + routeRadius - 8); Canvas.SetTop(cutter, 31); _drawing.Children.Add(cutter);
        var center = new Ellipse { Width = 4, Height = 4, Fill = tool };
        Canvas.SetLeft(center, 64 + routeRadius - 2); Canvas.SetTop(center, 37); _drawing.Children.Add(center);
        _description.Text = isInside ? "保留孔外材料 · 蓝线为孔内刀心" : "保留轮廓内材料 · 蓝线为外侧刀心";
        AutomationProperties.SetName(this, _description.Text + "。通用示意，非实际轮廓。");
    }
}
