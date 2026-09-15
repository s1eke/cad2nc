using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private InputNonClientPointerSource? _titleBarInput;
    private XamlRoot? _titleBarXamlRoot;
    private TitleBarRegions? _titleBarRegions;
    private bool _titleBarClosed;

    private void InitializeTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        // SetTitleBar(UIElement) also owns the caption rectangles. Use a single
        // explicit region owner so XAML layout cannot replace these drag regions.
        ShellHeader.Loaded += TitleBar_Loaded;
        ShellHeader.LayoutUpdated += TitleBar_LayoutUpdated;
        Closed += (_, _) =>
        {
            _titleBarClosed = true;
            ShellHeader.Loaded -= TitleBar_Loaded;
            ShellHeader.LayoutUpdated -= TitleBar_LayoutUpdated;
            if (_titleBarXamlRoot is not null) _titleBarXamlRoot.Changed -= TitleBarRoot_Changed;
        };
    }

    private void TitleBar_Loaded(object sender, RoutedEventArgs args)
    {
        if (_titleBarXamlRoot is not null) _titleBarXamlRoot.Changed -= TitleBarRoot_Changed;
        _titleBarXamlRoot = Root.XamlRoot;
        if (_titleBarXamlRoot is not null) _titleBarXamlRoot.Changed += TitleBarRoot_Changed;
        UpdateTitleBarRegions();
    }

    private void TitleBar_LayoutUpdated(object? sender, object args) => UpdateTitleBarRegions();
    private void TitleBarRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBarRegions();

    private void UpdateHeaderCommandWidth()
    {
        var width = Root.ActualWidth;
        var reserved = ShellHeader.Padding.Left + ShellHeader.Padding.Right + ShellHeader.ColumnSpacing;
        HeaderToolbar.Width = Math.Clamp(width - reserved - (width < 900 ? 108 : 250), 110, 580);
    }

    private void UpdateTitleBarRegions()
    {
        if (_titleBarClosed || !ShellHeader.IsLoaded || Root.XamlRoot is not { } xamlRoot ||
            ShellHeader.ActualWidth <= 0 || ShellHeader.ActualHeight <= 0) return;
        var scale = xamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0) return;
        var nativeTitleBar = AppWindow.TitleBar;
        var padding = new Thickness(Math.Max(12, nativeTitleBar.LeftInset / scale), 0,
            Math.Max(148, nativeTitleBar.RightInset / scale), 0);
        if (ShellHeader.Padding != padding)
        {
            ShellHeader.Padding = padding;
            UpdateHeaderCommandWidth();
            return; // Wait for the new padding and command positions to be arranged.
        }

        var regions = TitleBarRegionGeometry.Create(ShellHeader.ActualWidth, ShellHeader.ActualHeight,
            scale, nativeTitleBar.LeftInset, nativeTitleBar.RightInset, HeaderButtonBounds(MainCommands));
        if (_titleBarRegions is { } previous && previous.Caption.SequenceEqual(regions.Caption) &&
            previous.Passthrough.SequenceEqual(regions.Passthrough)) return;
        _titleBarInput ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _titleBarInput.SetRegionRects(NonClientRegionKind.Passthrough, regions.Passthrough.Select(NativeRect).ToArray());
        _titleBarInput.SetRegionRects(NonClientRegionKind.Caption, regions.Caption.Select(NativeRect).ToArray());
        _titleBarRegions = regions;
    }

    private IEnumerable<TitleBarRect> HeaderButtonBounds(DependencyObject node)
    {
        if (node is UIElement { Visibility: not Visibility.Visible }) yield break;
        if (node is ButtonBase button)
        {
            if (button.IsLoaded && button.IsHitTestVisible && button.XamlRoot == Root.XamlRoot &&
                button.ActualWidth > 0 && button.ActualHeight > 0)
            {
                var bounds = button.TransformToVisual(Root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                yield return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
            yield break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var bounds in HeaderButtonBounds(VisualTreeHelper.GetChild(node, index))) yield return bounds;
    }

    private static RectInt32 NativeRect(TitleBarPixelRect bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
