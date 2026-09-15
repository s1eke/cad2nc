using System.Numerics;
using Cad2Nc.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI;

namespace Cad2Nc.App.Controls;

public readonly record struct ScenePoint(double X, double Y);
public enum ScenePathKind { Source, Stock, Finished, Rapid, Toolpath }
public sealed record ScenePath(string Id, IReadOnlyList<ScenePoint> Points, ScenePathKind Kind,
    bool IsMachined = true, int Order = 0, bool HitTestInterior = false);
public sealed record ContourPickedEventArgs(string Id, bool ExtendSelection);
public sealed record CanvasViewport(double CenterX, double CenterY, double Scale);

/// <summary>GPU rendered 2D inspection view. CAD coordinates remain doubles until viewport conversion.</summary>
public sealed class CadCanvas : UserControl
{
    private readonly CanvasControl _canvas = new();
    private IReadOnlyList<ScenePath> _paths = [];
    private readonly HashSet<string> _focused = [];
    private double _referenceX, _referenceY, _centerX, _centerY, _scale = 1;
    private bool _pendingFit = true, _dragging;
    private Windows.Foundation.Point _lastPointer;
    private bool _showSource = true, _showFinished = true, _showRapid = true;
    private bool _showToolpaths = true, _showDirections = true, _staticDirty = true, _released, _isPlanStale;
    private ScenePoint _origin;
    private string _originLabel = "CAD";
    private string _emptyTitle = "打开文件，开始工作";
    private string _emptySubtitle = "打开 DWG / DXF 生成刀路，或打开 NC 查看轨迹";
    private CanvasRenderTarget? _staticImage, _travelledImage;
    private double _cacheWidth, _cacheHeight;
    private float _cacheDpi;
    private NcPlaybackProgram? _playbackProgram;
    private NcPlaybackFrame? _playbackFrame;
    private ScenePoint _playbackOrigin;
    private double _toolDiameter;
    private int _completedMotionCount;
    private bool _travelledDirty = true;
    // Identical XY cuts recur at every depth. Draw each once in the accumulated image.
    private readonly HashSet<(double Ax, double Ay, double Bx, double By, NcMotionKind Kind)> _travelledSegments = [];
    private static readonly Color CanvasBackground = Color.FromArgb(255, 13, 20, 31);
    private static readonly Color GridColor = Color.FromArgb(255, 29, 40, 55);
    private static readonly Color TextColor = Color.FromArgb(255, 141, 159, 182);
    private static readonly Color PlayedLinear = Color.FromArgb(255, 90, 222, 174);
    private static readonly Color PlayedRapid = Color.FromArgb(255, 238, 172, 94);
    private readonly CanvasTextFormat _labelFormat = new() { FontFamily = "Consolas", FontSize = 11 };
    private readonly CanvasStrokeStyle _dashed = new() { DashStyle = CanvasDashStyle.Dash };

    public event EventHandler<ContourPickedEventArgs>? ContourPicked;
    public event EventHandler<ScenePoint>? CoordinateChanged;
    public event EventHandler<CanvasViewport>? ViewportChanged;
    public ScenePoint Origin { get => _origin; set { if (_origin == value) return; _origin = value; InvalidateScene(); } }
    public string OriginLabel { get => _originLabel; set { if (_originLabel == value) return; _originLabel = value; InvalidateScene(); } }
    public string EmptyTitle { get => _emptyTitle; set { if (_emptyTitle == value) return; _emptyTitle = value; InvalidateScene(); } }
    public string EmptySubtitle { get => _emptySubtitle; set { if (_emptySubtitle == value) return; _emptySubtitle = value; InvalidateScene(); } }
    public bool ShowToolpaths { get => _showToolpaths; set { if (_showToolpaths == value) return; _showToolpaths = value; InvalidateScene(true); } }
    public bool ShowDirections { get => _showDirections; set { if (_showDirections == value) return; _showDirections = value; InvalidateScene(); } }
    public bool ShowSource { get => _showSource; set { if (_showSource == value) return; _showSource = value; InvalidateScene(); } }
    public bool ShowFinished { get => _showFinished; set { if (_showFinished == value) return; _showFinished = value; InvalidateScene(); } }
    public bool ShowRapid { get => _showRapid; set { if (_showRapid == value) return; _showRapid = value; InvalidateScene(true); } }
    /// <summary>Presentation state is independent of geometry identity and the workbench phase.</summary>
    public bool IsPlanStale { get => _isPlanStale; set { if (_isPlanStale == value) return; _isPlanStale = value; InvalidateScene(); } }

    public CadCanvas()
    {
        IsTabStop = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "刀路预览。滚轮缩放，中键拖动，单击选择轮廓，F 适应窗口。");
        Content = _canvas;
        _canvas.ClearColor = CanvasBackground;
        // Win2D raises CreateResources again after device loss and DPI changes.
        _canvas.CreateResources += (_, _) => ReleaseDrawingCaches();
        _canvas.Draw += Draw;
        _canvas.SizeChanged += (_, _) => { if (_pendingFit) FitToContent(); else InvalidateScene(true); };
        Unloaded += (_, _) => ReleaseDrawingCaches();
        _canvas.PointerWheelChanged += Wheel;
        _canvas.PointerPressed += Pressed;
        _canvas.PointerMoved += Moved;
        _canvas.PointerReleased += Released;
        _canvas.PointerCaptureLost += (_, _) => _dragging = false;
        KeyDown += Keyboard;
    }

    public void SetScene(IReadOnlyList<ScenePath> paths, bool fit = false)
    {
        // Keeping a stable double precision reference preserves the viewport across selection edits.
        var worldCenterX = _centerX + _referenceX;
        var worldCenterY = _centerY + _referenceY;
        _paths = paths;
        if (fit && paths.Any(p => p.Points.Count > 0))
        {
            _referenceX = paths.Where(p => p.Points.Count > 0).Min(p => p.Points.Min(v => v.X));
            _referenceY = paths.Where(p => p.Points.Count > 0).Min(p => p.Points.Min(v => v.Y));
            _centerX = worldCenterX - _referenceX;
            _centerY = worldCenterY - _referenceY;
        }
        if (fit || _pendingFit) FitToContent(); else InvalidateScene();
    }

    public void SetFocused(IEnumerable<string> ids)
    {
        _focused.Clear();
        _focused.UnionWith(ids);
        InvalidateScene();
    }

    /// <summary>Install only the generated NC program; its coordinates are relative to the supplied G54 origin.</summary>
    public void SetPlayback(NcPlaybackProgram? program, ScenePoint origin, double toolDiameter)
    {
        if (ReferenceEquals(program, _playbackProgram) && origin == _playbackOrigin && toolDiameter == _toolDiameter) return;
        _playbackProgram = program;
        _playbackOrigin = origin;
        _toolDiameter = double.IsFinite(toolDiameter) ? Math.Max(0, toolDiameter) : 0;
        _playbackFrame = null;
        ResetTravelled();
        InvalidateCanvas();
    }

    /// <summary>Update the animated overlay without rebuilding the static CAD drawing.</summary>
    public void UpdatePlayback(NcPlaybackFrame? frame)
    {
        if (_playbackProgram is null || frame?.MotionIndex < 0 || frame?.MotionIndex >= _playbackProgram.Motions.Length) frame = null;
        if (frame is null || frame.MotionIndex < _completedMotionCount) ResetTravelled();
        _playbackFrame = frame;
        InvalidateCanvas();
    }

    private void InvalidateCanvas() { if (!_released) _canvas.Invalidate(); }

    private void InvalidateScene(bool resetTravelled = false)
    {
        _staticDirty = true;
        if (resetTravelled) ResetTravelled();
        InvalidateCanvas();
    }

    private void ResetTravelled()
    {
        _completedMotionCount = 0;
        _travelledDirty = true;
        _travelledSegments.Clear();
    }

    private void ReleaseDrawingCaches()
    {
        _staticImage?.Dispose(); _staticImage = null;
        _travelledImage?.Dispose(); _travelledImage = null;
        _staticDirty = true;
        ResetTravelled();
    }

    public CanvasViewport GetViewport() => new(_centerX + _referenceX, _centerY + _referenceY, _scale);
    public void RestoreViewport(CanvasViewport viewport)
    {
        if (!double.IsFinite(viewport.CenterX) || !double.IsFinite(viewport.CenterY) || !double.IsFinite(viewport.Scale) || viewport.Scale <= 0) return;
        _centerX = viewport.CenterX - _referenceX; _centerY = viewport.CenterY - _referenceY;
        _scale = Math.Clamp(viewport.Scale, 1e-7, 1e7); _pendingFit = false; InvalidateScene(true);
    }
    private void NotifyViewport() => ViewportChanged?.Invoke(this, GetViewport());

    public void FitToContent() => FitPaths(_paths);

    public void FitToSelection()
    {
        var selected = _paths.Where(p => _focused.Contains(p.Id) && p.Kind != ScenePathKind.Rapid).ToArray();
        if (selected.Length > 0) FitPaths(selected);
    }

    private void FitPaths(IEnumerable<ScenePath> paths)
    {
        if (_canvas.ActualWidth < 10 || _canvas.ActualHeight < 10) { _pendingFit = true; return; }
        var nonempty = paths.Where(p => p.Points.Count > 0).ToArray();
        if (nonempty.Length == 0) { _pendingFit = true; InvalidateScene(true); return; }
        var minX = nonempty.Min(p => p.Points.Min(v => v.X));
        var minY = nonempty.Min(p => p.Points.Min(v => v.Y));
        var maxX = nonempty.Max(p => p.Points.Max(v => v.X));
        var maxY = nonempty.Max(p => p.Points.Max(v => v.Y));
        _referenceX = minX;
        _referenceY = minY;
        _centerX = (maxX - minX) / 2;
        _centerY = (maxY - minY) / 2;
        var padding = Math.Clamp(Math.Min(_canvas.ActualWidth, _canvas.ActualHeight) * .1, 12, 54);
        _scale = Math.Clamp(Math.Min(Math.Max(20, _canvas.ActualWidth - padding * 2) / Math.Max(maxX - minX, 1),
            Math.Max(20, _canvas.ActualHeight - padding * 2) / Math.Max(maxY - minY, 1)), 1e-7, 1e7);
        _pendingFit = false;
        InvalidateScene(true);
        NotifyViewport();
    }

    private Vector2 Screen(ScenePoint p) => new(
        (float)((p.X - _referenceX - _centerX) * _scale + _canvas.ActualWidth / 2),
        (float)(_canvas.ActualHeight / 2 - (p.Y - _referenceY - _centerY) * _scale));
    private ScenePoint World(double x, double y) => new(
        (x - _canvas.ActualWidth / 2) / _scale + _centerX + _referenceX,
        (_canvas.ActualHeight / 2 - y) / _scale + _centerY + _referenceY);

    private bool IsVisible(ScenePath path) => path.Kind switch
    {
        ScenePathKind.Source or ScenePathKind.Stock => ShowSource,
        ScenePathKind.Finished => ShowFinished,
        ScenePathKind.Rapid => ShowRapid,
        ScenePathKind.Toolpath => ShowToolpaths,
        _ => true
    };

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_released || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
        if (_staticImage is null || _travelledImage is null || _cacheWidth != _canvas.ActualWidth ||
            _cacheHeight != _canvas.ActualHeight || _cacheDpi != sender.Dpi)
        {
            ReleaseDrawingCaches();
            _cacheWidth = _canvas.ActualWidth; _cacheHeight = _canvas.ActualHeight; _cacheDpi = sender.Dpi;
            _staticImage = new CanvasRenderTarget(sender, (float)_cacheWidth, (float)_cacheHeight, _cacheDpi);
            _travelledImage = new CanvasRenderTarget(sender, (float)_cacheWidth, (float)_cacheHeight, _cacheDpi);
        }
        if (_staticDirty)
        {
            using var background = _staticImage.CreateDrawingSession();
            background.Clear(CanvasBackground);
            DrawStatic(background);
            _staticDirty = false;
        }
        UpdateTravelledImage();
        var ds = args.DrawingSession;
        ds.DrawImage(_staticImage);
        if (_playbackFrame is not null)
        {
            ds.DrawImage(_travelledImage);
            DrawPlayback(ds, _playbackFrame);
        }
    }

    private void DrawStatic(CanvasDrawingSession ds)
    {
        DrawGrid(ds);
        foreach (var path in _paths.OrderBy(p => p.Kind))
        {
            if (path.Points.Count == 0 || !IsVisible(path)) continue;
            if (path.Points.Count == 1)
            {
                var point = Screen(path.Points[0]);
                if (float.IsFinite(point.X) && float.IsFinite(point.Y) && point.X >= 0 && point.X <= _canvas.ActualWidth && point.Y >= 0 && point.Y <= _canvas.ActualHeight)
                    ds.FillCircle(point, 3, ResultColor(path.Kind, PlayedRapid));
                continue;
            }
            var focused = _focused.Contains(path.Id);
            var color = path.Kind switch
            {
                ScenePathKind.Stock => Color.FromArgb(255, 210, 165, 86),
                ScenePathKind.Finished => Color.FromArgb(255, 73, 191, 155),
                ScenePathKind.Rapid => Color.FromArgb(255, 67, 89, 118),
                ScenePathKind.Toolpath => Color.FromArgb(255, 74, 187, 249),
                _ => path.IsMachined ? Color.FromArgb(255, 123, 145, 171) : Color.FromArgb(255, 63, 80, 103)
            };
            if (focused) color = Color.FromArgb(255, 230, 245, 255);
            // Apply after focus styling so selecting an old result never makes it look current.
            color = ResultColor(path.Kind, color);
            var width = focused ? 2.6f : path.Kind == ScenePathKind.Toolpath ? 1.8f : 1.1f;
            var style = path.Kind is ScenePathKind.Source or ScenePathKind.Stock or ScenePathKind.Rapid ? _dashed : null;
            // Clip each segment in double precision before conversion to float; distant engineering
            // coordinates otherwise lose detail or overflow the GPU at extreme zoom levels.
            for (var i = 1; i < path.Points.Count; i++) DrawSegment(ds, path.Points[i - 1], path.Points[i], color, width, style);
            if (path.Kind == ScenePathKind.Toolpath && ShowDirections) DrawDirection(ds, path, color);
        }
        DrawOrigin(ds);
        if (_paths.Count == 0)
        {
            using var title = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 24, HorizontalAlignment = CanvasHorizontalAlignment.Center };
            using var subtitle = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 13, HorizontalAlignment = CanvasHorizontalAlignment.Center };
            var y = (float)_canvas.ActualHeight / 2;
            ds.DrawText(EmptyTitle, new Windows.Foundation.Rect(0, y - 44, _canvas.ActualWidth, 40), Color.FromArgb(255, 218, 229, 243), title);
            ds.DrawText(EmptySubtitle, new Windows.Foundation.Rect(0, y + 4, _canvas.ActualWidth, 28), TextColor, subtitle);
        }
    }

    private Color ResultColor(ScenePathKind kind, Color color) =>
        IsPlanStale && kind is ScenePathKind.Finished or ScenePathKind.Rapid or ScenePathKind.Toolpath
            ? Color.FromArgb((byte)Math.Round(color.A * .38), color.R, color.G, color.B)
            : color;

    private void DrawSegment(CanvasDrawingSession ds, ScenePoint a, ScenePoint b, Color color, float width, CanvasStrokeStyle? style)
    {
        var ax = (a.X - _referenceX - _centerX) * _scale + _canvas.ActualWidth / 2;
        var ay = _canvas.ActualHeight / 2 - (a.Y - _referenceY - _centerY) * _scale;
        var bx = (b.X - _referenceX - _centerX) * _scale + _canvas.ActualWidth / 2;
        var by = _canvas.ActualHeight / 2 - (b.Y - _referenceY - _centerY) * _scale;
        DrawScreenSegment(ds, ax, ay, bx, by, color, width, style);
    }

    private void DrawScreenSegment(CanvasDrawingSession ds, double ax, double ay, double bx, double by,
        Color color, float width, CanvasStrokeStyle? style)
    {
        if (!double.IsFinite(ax) || !double.IsFinite(ay) || !double.IsFinite(bx) || !double.IsFinite(by)) return;
        var dx = bx - ax; var dy = by - ay; var t0 = 0d; var t1 = 1d;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-20) return q >= 0;
            var t = q / p;
            if (p < 0) { if (t > t1) return false; t0 = Math.Max(t0, t); }
            else { if (t < t0) return false; t1 = Math.Min(t1, t); }
            return true;
        }
        if (!Clip(-dx, ax + 10) || !Clip(dx, _canvas.ActualWidth + 10 - ax) ||
            !Clip(-dy, ay + 10) || !Clip(dy, _canvas.ActualHeight + 10 - ay)) return;
        ds.DrawLine(new Vector2((float)(ax + t0 * dx), (float)(ay + t0 * dy)),
            new Vector2((float)(ax + t1 * dx), (float)(ay + t1 * dy)), color, width, style);
    }

    private (double X, double Y) PlaybackScreen(NcPosition position) => (
        (position.X + (_playbackOrigin.X - _referenceX) - _centerX) * _scale + _canvas.ActualWidth / 2,
        _canvas.ActualHeight / 2 - (position.Y + (_playbackOrigin.Y - _referenceY) - _centerY) * _scale);

    private void DrawPlaybackSegment(CanvasDrawingSession ds, NcPosition a, NcPosition b, Color color, float width,
        CanvasStrokeStyle? style = null)
    {
        // Subtract the large CAD reference before adding the NC offset so small NC details survive.
        var start = PlaybackScreen(a); var end = PlaybackScreen(b);
        DrawScreenSegment(ds, start.X, start.Y, end.X, end.Y, color, width, style);
    }

    private bool MotionVisible(NcMotionKind kind) => kind == NcMotionKind.Rapid ? ShowRapid : ShowToolpaths;

    private void UpdateTravelledImage()
    {
        if (_travelledImage is null) return;
        var targetCount = _playbackFrame?.MotionIndex ?? 0;
        if (_completedMotionCount > targetCount) ResetTravelled();
        if (!_travelledDirty && _completedMotionCount == targetCount) return;
        using var ds = _travelledImage.CreateDrawingSession();
        if (_travelledDirty) { ds.Clear(Color.FromArgb(0, 0, 0, 0)); _travelledDirty = false; }
        if (_playbackProgram is null) return;
        // A seek or zoom may require rebuilding a large trail. Spread it over frames instead of
        // walking up to 500,000 motion blocks synchronously on the UI thread.
        var batchEnd = Math.Min(targetCount, _completedMotionCount + 6_000);
        for (; _completedMotionCount < batchEnd; _completedMotionCount++)
        {
            var motion = _playbackProgram.Motions[_completedMotionCount];
            if (!MotionVisible(motion.Kind)) continue;
            var rapid = motion.Kind == NcMotionKind.Rapid;
            NcPosition? previous = null;
            foreach (var point in motion.GetPreviewPoints())
            {
                if (previous is { } prior)
                {
                    var a = prior; var b = point;
                    if (a.X != b.X || a.Y != b.Y)
                    {
                        if (a.X > b.X || (a.X == b.X && a.Y > b.Y)) (a, b) = (b, a);
                        if (_travelledSegments.Add((a.X, a.Y, b.X, b.Y, rapid ? NcMotionKind.Rapid : NcMotionKind.Linear)))
                            DrawPlaybackSegment(ds, a, b, rapid ? PlayedRapid : PlayedLinear, rapid ? 1.7f : 2.3f, rapid ? _dashed : null);
                    }
                }
                previous = point;
            }
        }
        if (_completedMotionCount < targetCount)
            DispatcherQueue.TryEnqueue(InvalidateCanvas);
    }

    private void DrawPlaybackMotion(CanvasDrawingSession ds, NcMotion motion, double progress, Color color, float width, CanvasStrokeStyle? style = null)
    {
        var count = Math.Max(1, motion.PreviewSegmentCount);
        var previous = motion.Start;
        var last = (int)Math.Ceiling(count * progress);
        for (var i = 1; i <= last; i++)
        {
            var point = motion.Sample(Math.Min(progress, (double)i / count));
            DrawPlaybackSegment(ds, previous, point, color, width, style);
            previous = point;
        }
    }

    private void DrawPlayback(CanvasDrawingSession ds, NcPlaybackFrame frame)
    {
        var motion = frame.Motion;
        var color = motion.Kind == NcMotionKind.Rapid ? PlayedRapid : PlayedLinear;
        if (MotionVisible(motion.Kind))
        {
            var remainingColor = Color.FromArgb(95, color.R, color.G, color.B);
            DrawPlaybackMotion(ds, motion, 1, remainingColor, 4);
            DrawPlaybackMotion(ds, motion, frame.MotionProgress, color, 3, motion.Kind == NcMotionKind.Rapid ? _dashed : null);
        }
        var point = PlaybackScreen(frame.Position);
        var radius = _toolDiameter * _scale / 2;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(radius) ||
            point.X + radius < -8 || point.X - radius > _canvas.ActualWidth + 8 ||
            point.Y + radius < -8 || point.Y - radius > _canvas.ActualHeight + 8) return;
        var center = new Vector2((float)point.X, (float)point.Y);
        if (radius > 0)
        {
            ds.FillCircle(center, (float)radius, Color.FromArgb(48, color.R, color.G, color.B));
            ds.DrawCircle(center, (float)radius, color, 2);
        }
        // The center stays readable even when the physical tool diameter is subpixel.
        if (point.X >= -8 && point.X <= _canvas.ActualWidth + 8 && point.Y >= -8 && point.Y <= _canvas.ActualHeight + 8)
        {
            ds.DrawLine(center - new Vector2(7, 0), center + new Vector2(7, 0), color, 1.2f);
            ds.DrawLine(center - new Vector2(0, 7), center + new Vector2(0, 7), color, 1.2f);
            ds.FillCircle(center, 2.5f, Color.FromArgb(255, 255, 255, 255));
        }
    }

    private void DrawGrid(CanvasDrawingSession ds)
    {
        var target = 90 / _scale;
        var power = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var norm = target / power;
        var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * power;
        var bottomLeft = World(0, _canvas.ActualHeight); var topRight = World(_canvas.ActualWidth, 0);
        var startX = Math.Floor((bottomLeft.X - Origin.X) / step) * step;
        var startY = Math.Floor((bottomLeft.Y - Origin.Y) / step) * step;
        for (var i = 0; i < 300; i++)
        {
            var x = startX + i * step; if (x > topRight.X - Origin.X) break;
            var sx = Screen(new ScenePoint(x + Origin.X, bottomLeft.Y)).X;
            ds.DrawLine(sx, 0, sx, (float)_canvas.ActualHeight, GridColor);
            ds.DrawText(x.ToString("0.###"), sx + 4, 8, TextColor, _labelFormat);
        }
        for (var i = 0; i < 300; i++)
        {
            var y = startY + i * step; if (y > topRight.Y - Origin.Y) break;
            var sy = Screen(new ScenePoint(bottomLeft.X, y + Origin.Y)).Y;
            ds.DrawLine(0, sy, (float)_canvas.ActualWidth, sy, GridColor);
            ds.DrawText(y.ToString("0.###"), 8, sy + 3, TextColor, _labelFormat);
        }
    }

    private void DrawDirection(CanvasDrawingSession ds, ScenePath path, Color color)
    {
        var start = Screen(path.Points[0]);
        if (start.X >= -20 && start.Y >= -20 && start.X <= _canvas.ActualWidth + 20 && start.Y <= _canvas.ActualHeight + 20)
        {
            ds.FillCircle(start, 3.3f, color);
            if (path.Order > 0) ds.DrawText(path.Order.ToString(), start + new Vector2(7, -20), color, _labelFormat);
        }
        var lengths = new double[path.Points.Count - 1]; var total = 0d;
        for (var i = 0; i < lengths.Length; i++) { var a = path.Points[i]; var b = path.Points[i + 1]; lengths[i] = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)); total += lengths[i]; }
        var target = total * .36;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (target > lengths[i] || lengths[i] < 1e-12) { target -= lengths[i]; continue; }
            var a = path.Points[i]; var b = path.Points[i + 1]; var t = target / lengths[i];
            var mid = Screen(new ScenePoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            if (mid.X < -20 || mid.Y < -20 || mid.X > _canvas.ActualWidth + 20 || mid.Y > _canvas.ActualHeight + 20) return;
            var direction = Vector2.Normalize(new Vector2((float)(b.X - a.X), (float)(a.Y - b.Y)));
            var normal = new Vector2(-direction.Y, direction.X);
            ds.DrawLine(mid - direction * 8, mid + direction * 8, color, 2);
            ds.DrawLine(mid + direction * 8, mid + direction * 2 + normal * 4, color, 2);
            ds.DrawLine(mid + direction * 8, mid + direction * 2 - normal * 4, color, 2);
            return;
        }
    }

    private void DrawOrigin(CanvasDrawingSession ds)
    {
        if (_paths.Count > 0)
        {
            var origin = Screen(Origin);
            if (origin.X > -30 && origin.Y > -30 && origin.X < _canvas.ActualWidth + 30 && origin.Y < _canvas.ActualHeight + 30)
            {
                ds.DrawCircle(origin, 6, Color.FromArgb(255, 241, 195, 114), 1.5f);
                ds.DrawLine(origin - new Vector2(10, 0), origin + new Vector2(10, 0), TextColor);
                ds.DrawLine(origin - new Vector2(0, 10), origin + new Vector2(0, 10), TextColor);
                ds.DrawText(OriginLabel + " · 0, 0", origin + new Vector2(12, 8), TextColor, _labelFormat);
            }
        }
        var marker = new Vector2(30, (float)_canvas.ActualHeight - 30);
        ds.DrawLine(marker, marker + new Vector2(30, 0), Color.FromArgb(255, 230, 139, 136), 2);
        ds.DrawLine(marker, marker + new Vector2(0, -30), Color.FromArgb(255, 108, 193, 155), 2);
        ds.DrawText("X", marker + new Vector2(35, -7), TextColor, _labelFormat);
        ds.DrawText("Y", marker + new Vector2(-3, -48), TextColor, _labelFormat);
        ds.DrawText("mm", (float)_canvas.ActualWidth - 38, (float)_canvas.ActualHeight - 25, TextColor, _labelFormat);
    }

    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        Zoom(p.Position.X, p.Position.Y, Math.Pow(1.12, p.Properties.MouseWheelDelta / 120d));
        e.Handled = true;
    }
    private void Zoom(double x, double y, double factor)
    {
        var before = World(x, y);
        _scale = Math.Clamp(_scale * factor, 1e-7, 1e7);
        var after = World(x, y);
        _centerX += before.X - after.X; _centerY += before.Y - after.Y;
        InvalidateScene(true);
        NotifyViewport();
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var p = e.GetCurrentPoint(_canvas);
        if (p.Properties.IsMiddleButtonPressed || p.Properties.IsRightButtonPressed)
        { _dragging = true; _lastPointer = p.Position; _canvas.CapturePointer(e.Pointer); e.Handled = true; return; }
        if (!p.Properties.IsLeftButtonPressed) return;
        var hit = HitTest(p.Position.X, p.Position.Y);
        if (hit is not null) ContourPicked?.Invoke(this, new ContourPickedEventArgs(hit, (e.KeyModifiers & VirtualKeyModifiers.Control) != 0));
        e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        if (_dragging)
        { _centerX -= (p.X - _lastPointer.X) / _scale; _centerY += (p.Y - _lastPointer.Y) / _scale; _lastPointer = p; InvalidateScene(true); }
        CoordinateChanged?.Invoke(this, World(p.X, p.Y));
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    { _dragging = false; _canvas.ReleasePointerCapture(e.Pointer); NotifyViewport(); }
    private string? HitTest(double x, double y)
    {
        var world = World(x, y);
        foreach (var path in _paths.Where(p => p.HitTestInterior && p.Kind != ScenePathKind.Rapid && IsVisible(p)))
            if (ContainsPoint(path.Points, world)) return path.Id;

        var best = 9d * 9d; string? id = null;
        foreach (var path in _paths.Where(p => p.Kind != ScenePathKind.Rapid && IsVisible(p)))
        {
            for (var i = 1; i < path.Points.Count; i++)
            {
                var a = Screen(path.Points[i - 1]); var b = Screen(path.Points[i]);
                var dx = (double)b.X - a.X; var dy = (double)b.Y - a.Y;
                var length = dx * dx + dy * dy;
                var t = length < 1e-20 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
                var distance = Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2);
                if (distance < best) { best = distance; id = path.Id; }
            }
        }
        return id;
    }

    private static bool ContainsPoint(IReadOnlyList<ScenePoint> polygon, ScenePoint point)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        var previous = polygon[^1];
        foreach (var current in polygon)
        {
            if ((current.Y > point.Y) != (previous.Y > point.Y) &&
                point.X < (previous.X - current.X) * (point.Y - current.Y) /
                    (previous.Y - current.Y) + current.X)
                inside = !inside;
            previous = current;
        }
        return inside;
    }
    private void Keyboard(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F) FitToContent();
        else if (e.Key is VirtualKey.Add or VirtualKey.GamepadRightTrigger) Zoom(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2, 1.25);
        else if (e.Key == VirtualKey.Subtract) Zoom(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2, .8);
        else if (e.Key == VirtualKey.Left) _centerX -= 40 / _scale;
        else if (e.Key == VirtualKey.Right) _centerX += 40 / _scale;
        else if (e.Key == VirtualKey.Up) _centerY += 40 / _scale;
        else if (e.Key == VirtualKey.Down) _centerY -= 40 / _scale;
        else return;
        InvalidateScene(true); NotifyViewport(); e.Handled = true;
    }

    public void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        ReleaseDrawingCaches();
        _canvas.RemoveFromVisualTree(); _labelFormat.Dispose(); _dashed.Dispose();
    }
}
