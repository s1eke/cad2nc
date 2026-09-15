using System.ComponentModel;
using System.Diagnostics;
using Cad2Nc.App.Controls;
using Cad2Nc.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Cad2Nc.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private TimeSpan _lastPlaybackTick;
    private bool _syncingPlayback;
    private NcPlaybackProgram? _canvasPlayback;
    private NcPreviewRow[] _ncRows = [];
    private int _activeNcLine = -1;
    private long _lastNcScroll;

    private void InitializePlayback()
    {
        _playbackTimer = DispatcherQueue.CreateTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(1000d / 30);
        _playbackTimer.Tick += (_, _) =>
        {
            if (!_session.CanPlay) { _session.Playback.Pause(); return; }
            var now = _playbackClock.Elapsed;
            var elapsed = now - _lastPlaybackTick;
            _lastPlaybackTick = now;
            _session.Playback.Advance(elapsed);
            UpdatePlaybackUi();
        };
        _session.Playback.PropertyChanged += Playback_Changed;
        PlaybackProgress.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _session.Playback.Pause()), true);
    }

    private void Playback_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_session.Playback.IsPlaying))
        {
            if (_session.Playback.IsPlaying)
            { _playbackClock.Restart(); _lastPlaybackTick = TimeSpan.Zero; _playbackTimer?.Start(); }
            else StopPlaybackTimer();
            UpdatePlaybackUi();
        }
        if (e.PropertyName == nameof(_session.Playback.Program)) RefreshPlaybackProgram();
    }

    private void StopPlaybackTimer()
    { _playbackTimer?.Stop(); _playbackClock.Stop(); }

    private void RefreshPlaybackProgram()
    {
        var program = _session.Playback.Program;
        if (!ReferenceEquals(program, _canvasPlayback))
        {
            _canvasPlayback = program;
            var origin = _session.Plan?.Origin ?? _session.SelectedBatchProgram?.Plan.Origin;
            Preview.SetPlayback(program, new ScenePoint(origin?.X ?? 0, origin?.Y ?? 0), _session.IsNcDocument ? 0 : _session.Config.Tool.Diameter);
        }
        UpdatePlaybackUi();
    }

    private void SetNcRows(IReadOnlyList<string> lines)
    {
        _ncRows = lines.Select((code, index) => new NcPreviewRow(code, index + 1)).ToArray();
        _activeNcLine = -1; NcList.ItemsSource = _ncRows;
    }

    private void UpdatePlaybackUi()
    {
        if (PlaybackButton is null) return;
        var playback = _session.Playback;
        var batch = _session.IsBatchMode && _session.SelectedBatchProgram is null;
        PlaybackButton.Visibility = PlaybackResetButton.Visibility = PlaybackProgress.Visibility = PlaybackSpeed.Visibility =
            batch ? Visibility.Collapsed : Visibility.Visible;
        if (batch)
        {
            PlaybackStatusText.Text = "多母板结果请选择一块母板以预览 NC；导出时每块母板生成独立 NC。";
            PlaybackPositionText.Text = "多母板静态预览";
            Preview.UpdatePlayback(null);
            return;
        }
        var ready = _session.CanPlay;
        PlaybackButton.IsEnabled = ready;
        PlaybackResetButton.IsEnabled = ready;
        PlaybackProgress.IsEnabled = ready;
        PlaybackSpeed.IsEnabled = ready;
        PlaybackButton.Content = playback.IsPlaying ? "暂停" : "播放";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaybackButton, playback.IsPlaying ? "暂停 NC" : "播放 NC");
        _syncingPlayback = true;
        try { PlaybackProgress.Value = playback.Progress * 100; }
        finally { _syncingPlayback = false; }
        var frame = playback.Frame;
        Preview.UpdatePlayback(frame);
        var line = frame?.Motion.LineIndex ?? -1;
        if (line != _activeNcLine)
        {
            if (_activeNcLine >= 0 && _activeNcLine < _ncRows.Length) _ncRows[_activeNcLine].IsActive = false;
            _activeNcLine = line;
            if (line >= 0 && line < _ncRows.Length)
            {
                _ncRows[line].IsActive = true;
                var timestamp = Environment.TickCount64;
                if (!playback.IsPlaying || timestamp - _lastNcScroll >= 200)
                { NcList.ScrollIntoView(_ncRows[line], ScrollIntoViewAlignment.Leading); _lastNcScroll = timestamp; }
            }
        }
        if (frame is null)
        {
            PlaybackPositionText.Text = _session.PlaybackError is { } error ? $"无法播放：{error}" : "位置 —";
            PlaybackStatusText.Text = "";
            return;
        }
        var p = frame.Position;
        var motion = frame.Motion;
        PlaybackPositionText.Text = $"X {p.X:0.000}  Y {p.Y:0.000}  " + (p.IsZKnown ? $"Z {p.Z:0.000} mm" : "Z 未指定（XY 单位 mm）");
        var changesZ = motion.Start.IsZKnown && motion.End.IsZKnown && Math.Abs(motion.End.Z - motion.Start.Z) > .00001;
        var changesXY = Math.Abs(motion.End.X - motion.Start.X) > .00001 || Math.Abs(motion.End.Y - motion.Start.Y) > .00001;
        var action = motion.Kind == NcMotionKind.Rapid ? "空移 / 抬刀" : motion.Arc is not null ? "圆弧切削" : changesZ ? changesXY ? "斜向下刀" : "垂直下刀" : "切削";
        var source = _session.IsNcDocument
            ? $"NC 第 {motion.LineIndex + 1} 行"
            : _session.SelectedBatchProgram is { } batchProgram
                ? $"母板 {batchProgram.SheetContourId} · NC 第 {motion.LineIndex + 1} 行"
            : motion.OperationNumber > 0
                ? $"刀次 {motion.OperationNumber}/{motion.OperationCount} · 路径 {motion.PathNumber} · 第 {motion.PassNumber}/{motion.PassCount} 层 · NC 第 {motion.LineIndex + 1} 行"
                : $"轮廓 {motion.PathNumber} · 第 {motion.PassNumber}/{motion.PassCount} 层 · NC 第 {motion.LineIndex + 1} 行";
        PlaybackStatusText.Text = $"{action} · {source} · " +
            $"{FormatPlaybackTime(playback.PositionSeconds)} / {FormatPlaybackTime(playback.Program!.DurationSeconds)}";
    }

    private void NcLine_Click(object sender, ItemClickEventArgs e)
    {
        if (!_session.CanPlay || e.ClickedItem is not NcPreviewRow row || _session.Playback.Program is not { } program) return;
        _session.Playback.Pause();
        var motion = program.Motions.FirstOrDefault(m => m.LineIndex >= row.LineNumber - 1);
        var seconds = motion?.StartSeconds ?? program.DurationSeconds;
        _session.Playback.Seek(program.DurationSeconds <= 0 ? 0 : seconds / program.DurationSeconds);
        UpdatePlaybackUi();
    }

    private static string FormatPlaybackTime(double seconds) => seconds >= 86_400_000
        ? $"{seconds / 86400:0.##E+0} 天"
        : $"{Math.Floor(seconds / 60):00}:{(int)(seconds % 60):00}";

    private void Playback_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.CanPlay) return;
        if (_session.Playback.IsPlaying) _session.Playback.Pause(); else _session.Playback.Play();
        UpdatePlaybackUi();
    }
    private void PlaybackReset_Click(object sender, RoutedEventArgs e)
    { _session.Playback.Reset(); UpdatePlaybackUi(); }
    private void PlaybackProgress_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPlayback || !_session.CanPlay) return;
        _session.Playback.Pause();
        _session.Playback.Seek(e.NewValue / 100); UpdatePlaybackUi();
    }
    private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_playbackTimer is null || PlaybackSpeed.SelectedIndex < 0) return;
        double[] speeds = [.25, .5, 1, 2, 5, 10];
        _session.Playback.SetSpeed(speeds[PlaybackSpeed.SelectedIndex]);
        UpdatePlaybackUi();
    }

    public sealed class NcPreviewRow(string code, int lineNumber) : ObservableObject
    {
        private static readonly Brush ActiveBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 77, 101));
        private static readonly Brush ClearBrush = new SolidColorBrush(Colors.Transparent);
        private bool active;
        public string Code { get; } = code;
        public int LineNumber { get; } = lineNumber;
        public bool IsActive { get => active; set { if (SetProperty(ref active, value)) OnPropertyChanged(nameof(ActiveBackground)); } }
        public Brush ActiveBackground => IsActive ? ActiveBrush : ClearBrush;
    }
}
