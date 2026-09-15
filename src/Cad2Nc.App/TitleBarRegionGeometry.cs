namespace Cad2Nc.App;

internal readonly record struct TitleBarRect(double X, double Y, double Width, double Height);

internal readonly record struct TitleBarPixelRect(int X, int Y, int Width, int Height);

internal sealed record TitleBarRegions(TitleBarPixelRect[] Caption, TitleBarPixelRect[] Passthrough);

internal static class TitleBarRegionGeometry
{
    public static TitleBarRegions Create(
        double width,
        double height,
        double scale,
        int leftInset,
        int rightInset,
        IEnumerable<TitleBarRect> interactiveBounds)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scale)
            || width <= 0 || height <= 0 || scale <= 0)
            return new([], []);

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;
        if (!double.IsFinite(scaledWidth) || !double.IsFinite(scaledHeight)
            || scaledWidth > int.MaxValue || scaledHeight > int.MaxValue)
            return new([], []);

        // Keep every registered pixel inside the header and outside the system caption buttons.
        var pixelWidth = (int)Math.Floor(scaledWidth);
        var pixelHeight = (int)Math.Floor(scaledHeight);
        var left = Math.Clamp(leftInset, 0, pixelWidth);
        var right = pixelWidth - Math.Clamp(rightInset, 0, pixelWidth);
        if (right <= left || pixelHeight <= 0)
            return new([], []);

        var caption = new List<TitleBarPixelRect> { new(left, 0, right - left, pixelHeight) };
        var passthrough = new List<TitleBarPixelRect>();
        foreach (var bounds in interactiveBounds)
        {
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
                || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
                || bounds.Width <= 0 || bounds.Height <= 0)
                continue;

            var clippedLeft = Math.Max(0, bounds.X);
            var clippedTop = Math.Max(0, bounds.Y);
            var clippedRight = Math.Min(width, bounds.X + bounds.Width);
            var clippedBottom = Math.Min(height, bounds.Y + bounds.Height);
            if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
                continue;

            // Round buttons outwards so fractional DPI never assigns an interactive edge to dragging.
            var buttonLeft = Math.Clamp((int)Math.Floor(clippedLeft * scale), left, right);
            var buttonTop = Math.Clamp((int)Math.Floor(clippedTop * scale), 0, pixelHeight);
            var buttonRight = Math.Clamp((int)Math.Ceiling(clippedRight * scale), left, right);
            var buttonBottom = Math.Clamp((int)Math.Ceiling(clippedBottom * scale), 0, pixelHeight);
            if (buttonRight <= buttonLeft || buttonBottom <= buttonTop)
                continue;

            var button = new TitleBarPixelRect(buttonLeft, buttonTop, buttonRight - buttonLeft, buttonBottom - buttonTop);
            passthrough.Add(button);
            var remainder = new List<TitleBarPixelRect>();
            foreach (var region in caption)
                Subtract(region, button, remainder);
            caption = remainder;
        }

        return new(caption.ToArray(), passthrough.Distinct().ToArray());
    }

    private static void Subtract(TitleBarPixelRect region, TitleBarPixelRect button, List<TitleBarPixelRect> result)
    {
        var left = Math.Max(region.X, button.X);
        var top = Math.Max(region.Y, button.Y);
        var right = Math.Min(region.X + region.Width, button.X + button.Width);
        var bottom = Math.Min(region.Y + region.Height, button.Y + button.Height);
        if (right <= left || bottom <= top)
        {
            result.Add(region);
            return;
        }

        if (top > region.Y)
            result.Add(new(region.X, region.Y, region.Width, top - region.Y));
        if (bottom < region.Y + region.Height)
            result.Add(new(region.X, bottom, region.Width, region.Y + region.Height - bottom));
        if (left > region.X)
            result.Add(new(region.X, top, left - region.X, bottom - top));
        if (right < region.X + region.Width)
            result.Add(new(right, top, region.X + region.Width - right, bottom - top));
    }
}
