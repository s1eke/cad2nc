using Cad2Nc.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cad2Nc.Application;

/// <summary>UI-independent playback clock. Advance receives real elapsed time, not a frame count.</summary>
public sealed class NcPlaybackController : ObservableObject
{
    public NcPlaybackProgram? Program { get; private set; }
    public NcPlaybackFrame? Frame { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool CanPlay => Program is { DurationSeconds: > 0 };
    public double PositionSeconds { get; private set; }
    public double Progress => Program is { DurationSeconds: > 0 } program ? PositionSeconds / program.DurationSeconds : 0;
    public double Speed { get; private set; } = 10;

    public void Load(NcPlaybackProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        Program = program; PositionSeconds = 0; IsPlaying = false; Frame = program.Sample(0);
        NotifyProgram();
    }

    public void Clear()
    {
        Program = null; Frame = null; PositionSeconds = 0; IsPlaying = false;
        NotifyProgram();
    }

    public void Play()
    {
        if (!CanPlay || IsPlaying) return;
        if (PositionSeconds >= Program!.DurationSeconds) SetPosition(0);
        IsPlaying = true; OnPropertyChanged(nameof(IsPlaying));
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        IsPlaying = false; OnPropertyChanged(nameof(IsPlaying));
    }

    public void Reset()
    { Pause(); SetPosition(0); }

    /// <summary>Seek to a normalized progress; seeking retains the current paused/playing state.</summary>
    public void Seek(double progress)
    {
        if (!double.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        SetPosition(Math.Clamp(progress, 0, 1) * (Program?.DurationSeconds ?? 0));
    }

    public void SetSpeed(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier < .25 || multiplier > 10) throw new ArgumentOutOfRangeException(nameof(multiplier), "播放速度应在 0.25 至 10 倍之间。");
        if (Speed == multiplier) return;
        Speed = multiplier; OnPropertyChanged(nameof(Speed));
    }

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (!IsPlaying || Program is null) return;
        SetPosition(Math.Min(Program.DurationSeconds, PositionSeconds + elapsed.TotalSeconds * Speed));
    }

    private void SetPosition(double seconds)
    {
        PositionSeconds = Math.Clamp(seconds, 0, Program?.DurationSeconds ?? 0);
        Frame = Program?.Sample(PositionSeconds);
        if (Program is { } program && PositionSeconds >= program.DurationSeconds) Pause();
        NotifyPosition();
    }

    private void NotifyProgram()
    {
        OnPropertyChanged(nameof(Program)); OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(IsPlaying)); NotifyPosition();
    }

    private void NotifyPosition()
    { OnPropertyChanged(nameof(PositionSeconds)); OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(Frame)); }
}
