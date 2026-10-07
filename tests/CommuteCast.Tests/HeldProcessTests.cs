using CommuteCast.Infrastructure;
using System.Diagnostics;

namespace CommuteCast.Tests;

public class HeldProcessTests
{
    [Fact] public async Task FfmpegWritesSeekableWaveThroughExclusiveHeldFile()
    {
        using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Parent, "held.wav");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=24000:duration=1", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", "-fd", "1", "fd:"], output, 100000, TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode); Assert.Equal(48000, WaveAudio.DataRegion(output).Length);
            Assert.Throws<IOException>(() => File.WriteAllText(path, "replacement"));
        }
        Assert.Equal(24000, WaveAudio.Inspect(path).Samples);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationAndTimeoutStopOwnedChildAndReleaseInheritedHandle(bool cancelCaller)
    {
        using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Parent, "held.wav");
        using var cancel = new CancellationTokenSource(); int pid = 0; var watch = Stopwatch.StartNew();
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var run = ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-nostdin", "-re", "-f", "lavfi", "-i", "sine=sample_rate=24000:duration=30", "-f", "wav", "-fd", "1", "fd:"], output, 2000000,
                TimeSpan.FromSeconds(cancelCaller ? 30 : 1), cancel.Token, child => { pid = child; if (cancelCaller) cancel.CancelAfter(500); });
            if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.NotEqual(0, pid); Assert.InRange(watch.Elapsed.TotalSeconds, .3, 8);
            try { using var child = Process.GetProcessById(pid); Assert.True(child.HasExited); } catch (ArgumentException) { }
        }
        File.WriteAllText(path, "released"); Assert.Equal("released", File.ReadAllText(path));
    }

    [Fact] public async Task OversizedOutputIsRejectedAndChildStopped()
    {
        using var workspace = new TestWorkspace();
        await using var output = new FileStream(Path.Combine(workspace.Parent, "held.wav"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=sample_rate=24000:duration=30", "-f", "wav", "-fd", "1", "fd:"], output, 100, TimeSpan.FromSeconds(10)));
    }

    [Fact] public async Task HeldMp3PreservesGaplessSampleCountAndQuotedUnicodeMetadata()
    {
        using var workspace = new TestWorkspace(); var source = Path.Combine(workspace.Parent, "source.wav"); TestWorkspace.WriteWave(source);
        var path = Path.Combine(workspace.Parent, "held.mp3"); const string title = "A quoted \"title\" 🚆 with trailing slash\\";
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await ProcessRunner.RunToFileAsync("ffmpeg", ["-v", "error", "-nostdin", "-i", source, "-c:a", "libmp3lame", "-b:a", "128k", "-metadata", "title=" + title, "-f", "mp3", "-fd", "1", "fd:"], output, 100000, TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
        }
        var decoded = Path.Combine(workspace.Parent, "decoded.wav");
        var decode = await ProcessRunner.RunAsync("ffmpeg", ["-v", "error", "-i", path, "-c:a", "pcm_s16le", decoded], TimeSpan.FromSeconds(10));
        Assert.Equal(0, decode.ExitCode); Assert.Equal(24000, WaveAudio.Inspect(decoded).Samples);
        var probe = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags=title", "-of", "json", path], TimeSpan.FromSeconds(10));
        Assert.Equal(0, probe.ExitCode); using var json = System.Text.Json.JsonDocument.Parse(probe.Output);
        Assert.Equal(title, json.RootElement.GetProperty("format").GetProperty("tags").GetProperty("title").GetString());
    }

    [Fact] public async Task HeldOutputDrainsLargeDiagnosticsWithoutGrowingCapture()
    {
        using var workspace = new TestWorkspace();
        await using var output = new FileStream(Path.Combine(workspace.Parent, "held.txt"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var result = await ProcessRunner.RunToFileAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write(('b' * 400000)); [Console]::Out.Write('done')"], output, 1000, TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode); Assert.Equal(256 * 1024, result.Error.Length);
        output.Position = 0; using var reader = new StreamReader(output, leaveOpen: true); Assert.Equal("done", await reader.ReadToEndAsync());
    }
}
