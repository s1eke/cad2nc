using System.Globalization;
using System.Text;
using Cad2Nc.Application;
using Cad2Nc.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using QRCoder;
using Windows.Storage.Streams;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Cad2Nc.App;

/// <summary>60×40 mm thermal-label visual shared by preview and PrintDocument.</summary>
internal static class BoardLabelPage
{
    private const double WidthDip = 60 / 25.4 * 96;
    private const double HeightDip = 40 / 25.4 * 96;

    public static async Task<Grid> CreateAsync(BoardLabelInfo label)
    {
        var root = new Grid
        {
            Width = WidthDip,
            Height = HeightDip,
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Colors.Black),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new TextBlock
        {
            Text = "CAD2NC · 订单标签",
            FontSize = 8,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Black),
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(header);

        var middle = new Grid { ColumnSpacing = 4 };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        identity.Children.Add(new TextBlock
        {
            Text = $"订单：{Trim(label.OrderName, 14)}",
            FontSize = 7,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.Black),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(new TextBlock
        {
            Text = $"板号 {label.BoardId}",
            FontSize = label.BoardId.Length > 5 ? 13 : 19,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.Black)
        });
        identity.Children.Add(new TextBlock
        {
            Text = $"第 {label.Index:00} 块 / 共 {label.Total:00} 块",
            FontSize = 6.5,
            Foreground = new SolidColorBrush(Colors.Black),
            TextWrapping = TextWrapping.Wrap
        });
        identity.Children.Add(new TextBlock
        {
            Text = $"板材 {label.BoardWidth:0.#}×{label.BoardHeight:0.#} mm",
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 5.8,
            Foreground = new SolidColorBrush(Colors.Black)
        });
        identity.Children.Add(new TextBlock
        {
            Text = "● 定位点见轮廓图",
            FontSize = 5.8,
            Foreground = new SolidColorBrush(Colors.Black)
        });
        Grid.SetColumn(identity, 0);
        middle.Children.Add(identity);

        var diagram = new Canvas { Width = 88, Height = 94, Background = new SolidColorBrush(Color.FromArgb(255, 248, 249, 250)) };
        DrawDiagram(diagram, label);
        Grid.SetColumn(diagram, 1);
        middle.Children.Add(diagram);

        var qrPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var qrImage = new Image { Width = 60, Height = 60, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        qrImage.Source = await CreateQrImageAsync(label.QrPayload);
        qrPanel.Children.Add(qrImage);
        qrPanel.Children.Add(new TextBlock
        {
            Text = "扫码核对订单",
            FontSize = 5.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.Black)
        });
        Grid.SetColumn(qrPanel, 2);
        middle.Children.Add(qrPanel);
        Grid.SetRow(middle, 1);
        root.Children.Add(middle);
        return root;
    }

    private static void DrawDiagram(Canvas canvas, BoardLabelInfo label)
    {
        var bounds = label.BoardBounds;
        var width = Math.Max(1e-6, bounds.Width);
        var height = Math.Max(1e-6, bounds.Height);
        const double margin = 4;
        var scale = Math.Min((canvas.Width - margin * 2) / width, (canvas.Height - margin * 2) / height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;
        var offsetX = (canvas.Width - drawWidth) / 2;
        var offsetY = (canvas.Height - drawHeight) / 2;

        foreach (var path in label.Paths)
        {
            if (path.Points.Length < 2) continue;
            var geometry = new PathGeometry();
            var figure = new PathFigure { IsClosed = true, IsFilled = false };
            figure.StartPoint = ToPoint(path.Points[0]);
            var segment = new PolyLineSegment();
            foreach (var point in path.Points.Skip(1)) segment.Points.Add(ToPoint(point));
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
            canvas.Children.Add(new XamlPath
            {
                Data = geometry,
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = path.IsStock ? 1.3 : path.IsHighlighted ? 1.8 : .7,
                Opacity = path.IsStock || path.IsHighlighted ? 1 : .38
            });
        }

        // Mark the physical orientation point without adding machining jargon.
        var originX = offsetX + (label.Origin.X - bounds.MinX) * scale;
        var originY = offsetY + (bounds.MaxY - label.Origin.Y) * scale;
        var marker = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = new SolidColorBrush(Colors.Black)
        };
        Canvas.SetLeft(marker, Math.Clamp(originX - 2, 0, canvas.Width - 4));
        Canvas.SetTop(marker, Math.Clamp(originY - 2, 0, canvas.Height - 4));
        canvas.Children.Add(marker);

        Windows.Foundation.Point ToPoint(Point2 point) => new(
            offsetX + (point.X - bounds.MinX) * scale,
            offsetY + (bounds.MaxY - point.Y) * scale);
    }

    private static async Task<BitmapImage> CreateQrImageAsync(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var bytes = new PngByteQRCode(data).GetGraphic(5, drawQuietZones: true);
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

}
