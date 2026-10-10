using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommuteCast.Core;

namespace CommuteCast.Desktop;

public interface IPlaybackOutput
{
    event Action? Opened;
    event Action? Ended;
    event Action? Failed;
    double Duration { get; }
    double Position { get; set; }
    void Open(string path);
    void Play();
    void Pause();
    void Close();
}

internal sealed class WpfPlaybackOutput : IPlaybackOutput
{
    private readonly MediaPlayer player;
    public event Action? Opened;
    public event Action? Ended;
    public event Action? Failed;
    public WpfPlaybackOutput(MediaPlayer player)
    {
        this.player = player;
        player.MediaOpened += (_, _) => Opened?.Invoke();
        player.MediaEnded += (_, _) => Ended?.Invoke();
        player.MediaFailed += (_, _) => Failed?.Invoke();
    }
    public double Duration => player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
    public double Position { get => player.Position.TotalSeconds; set => player.Position = TimeSpan.FromSeconds(value); }
    public void Open(string path) => player.Open(new Uri(path));
    public void Play() => player.Play();
    public void Pause() => player.Pause();
    public void Close() { player.Stop(); player.Close(); }
}

public sealed class PlaybackController : Observable, IDisposable
{
    private readonly IPlaybackOutput output;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private bool disposed;
    public string Title { get; private set; } = "No local audio playing";
    public double DurationSeconds { get; private set; }
    public bool CanControl => DurationSeconds > 0;
    public bool IsPlaying { get; private set; }
    public string PauseLabel => IsPlaying ? "Pause" : "Continue";
    public double PositionSeconds
    {
        get => CanControl ? PlaybackTime.Clamp(output.Position, DurationSeconds) : 0;
        set { if (CanControl && double.IsFinite(value) && Math.Abs(value - PositionSeconds) > .1) { output.Position = PlaybackTime.Clamp(value, DurationSeconds); UpdateClock(); } }
    }
    public string Clock => $"{PlaybackTime.Format(PositionSeconds)} / {PlaybackTime.Format(DurationSeconds)} · {PlaybackTime.Format(DurationSeconds - PositionSeconds)} remaining";
    public event Action? Finished;
    public event Action? Failed;
    public ICommand PauseCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public PlaybackController(IPlaybackOutput output)
    {
        this.output = output;
        output.Opened += Opened; output.Ended += Ended; output.Failed += MediaFailed;
        timer.Tick += (_, _) => UpdateClock();
        PauseCommand = new AsyncCommand(_ => { if (CanControl) { if (IsPlaying) output.Pause(); else output.Play(); IsPlaying = !IsPlaying; Raise(nameof(IsPlaying)); Raise(nameof(PauseLabel)); } return Task.CompletedTask; }, _ => MediaFailed());
        BackCommand = new AsyncCommand(_ => { PositionSeconds -= 15; return Task.CompletedTask; }, _ => MediaFailed());
        ForwardCommand = new AsyncCommand(_ => { PositionSeconds += 30; return Task.CompletedTask; }, _ => MediaFailed());
    }
    public void Open(string path, string title)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Stop(); Title = title; IsPlaying = true; Raise(null);
        output.Open(path); output.Play();
    }
    private void Opened()
    {
        DurationSeconds = double.IsFinite(output.Duration) && output.Duration > 0 ? output.Duration : 0;
        Raise(null); timer.Start();
    }
    private void Ended() { Stop(); Finished?.Invoke(); }
    private void MediaFailed() { Stop(); Failed?.Invoke(); }
    private void UpdateClock() { Raise(nameof(PositionSeconds)); Raise(nameof(Clock)); }
    public void Stop()
    {
        timer.Stop(); output.Close(); IsPlaying = false; DurationSeconds = 0; Title = "No local audio playing"; Raise(null);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); output.Opened -= Opened; output.Ended -= Ended; output.Failed -= MediaFailed;
    }
}
