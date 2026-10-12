using System.IO;
using NAudio.Wave;

namespace CommuteCast.Desktop;

internal interface IMicrophoneRecording : IDisposable
{
    void Start();
    Task<byte[]> StopAsync();
    event Action? LimitReached;
    event Action<Exception>? Failed;
}

/// <summary>Bounded PCM capture in memory. No private recording is written to disk.</summary>
internal sealed class MicrophoneRecording : IMicrophoneRecording
{
    private const int MaxPcmBytes = 16000 * 2 * 120;
    private readonly WaveInEvent input = new() { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
    private readonly MemoryStream pcm = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private bool stopping, disposed, limitReached;
    public event Action? LimitReached;
    public event Action<Exception>? Failed;
    internal long CapturedBytes { get { lock (gate) return pcm.Length; } }

    public MicrophoneRecording()
    {
        input.DataAvailable += OnData;
        input.RecordingStopped += OnStopped;
    }

    public void Start()
    {
        if (WaveInEvent.DeviceCount == 0) throw new IOException("No microphone is available. Connect one and enable Windows microphone access for desktop apps.");
        input.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        var reached = false;
        lock (gate)
        {
            if (disposed || limitReached) return;
            var remaining = MaxPcmBytes - (int)pcm.Length;
            pcm.Write(args.Buffer, 0, Math.Min(remaining, args.BytesRecorded));
            if (pcm.Length == MaxPcmBytes) { limitReached = true; reached = true; }
        }
        if (reached) LimitReached?.Invoke();
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is { } error) { stopped.TrySetException(error); Failed?.Invoke(error); }
        else stopped.TrySetResult();
    }

    public async Task<byte[]> StopAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!stopping) { stopping = true; input.StopRecording(); }
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (CapturedBytes < 3200) throw new IOException("No audio was captured. Check the Windows default input device and microphone access, then record for at least a second.");
        using var wav = new MemoryStream();
        // Dispose writes the RIFF sizes. IgnoreDisposeStream keeps the result readable.
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), input.WaveFormat))
        {
            lock (gate) { var data = pcm.ToArray(); writer.Write(data, 0, data.Length); }
        }
        return wav.ToArray();
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        input.DataAvailable -= OnData;
        input.RecordingStopped -= OnStopped;
        input.Dispose();
        stopped.TrySetCanceled();
        pcm.Dispose();
    }
}
