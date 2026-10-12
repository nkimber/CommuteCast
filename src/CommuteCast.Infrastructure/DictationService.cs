using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public interface IDictationService : IDisposable
{
    Task ReadyAsync(CancellationToken ct);
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
}

public sealed class DictationService : IDictationService
{
    public const string SetupHelp = "Open Docker Desktop, then run scripts\\Provision-Dictation.ps1 -Build from the CommuteCast folder. After setup, choose Check / repair dictation.";
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false })
    { BaseAddress = new("http://127.0.0.1:8767/"), Timeout = TimeSpan.FromMinutes(3) };

    public async Task ReadyAsync(CancellationToken ct)
    {
        try
        {
            var context = await DockerAsync(["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], ct);
            if (context.Trim() != "npipe:////./pipe/dockerDesktopLinuxEngine")
                throw new IOException("Select the local Docker Desktop Linux context.");
            var metadata = await DockerAsync(["inspect", "commutecast-whisper"], ct);
            var image = await DockerAsync(["image", "inspect", "commutecast-whisper:1", "--format", "{{.Id}}"], ct);
            using var document = JsonDocument.Parse(metadata);
            var container = document.RootElement[0];
            ValidateContainer(container, image.Trim());
            var state = container.GetProperty("State");
            if (state.GetProperty("Paused").GetBoolean() || state.GetProperty("OOMKilled").GetBoolean())
                throw new IOException("Dictation is paused or ran out of memory. Inspect it in Docker Desktop before restarting.");
            if (!state.GetProperty("Running").GetBoolean())
                await DockerAsync(["start", "commutecast-whisper"], ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var health = await http.GetFromJsonAsync<Health>("health", requestDeadline.Token);
                    if (health is { Service: "commutecast-whisper", Contract: 1, State: "ready", Model: "small.en", Revision: "d1d751a5f8271d482d14ca55d9e2deeebbae577f" }) return;
                    throw new IOException("The localhost dictation service returned an incompatible identity.");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(1000, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new IOException("Whisper did not become ready within 90 seconds. " + SetupHelp); }
        catch (Exception error) when (error is not OperationCanceledException)
        { throw new IOException("Local dictation is unavailable. " + error.Message + " " + SetupHelp, error); }
    }

    internal static void ValidateContainer(JsonElement container, string expectedImage)
    {
        var labels = container.GetProperty("Config").GetProperty("Labels");
        var ports = container.GetProperty("HostConfig").GetProperty("PortBindings");
        if (container.GetProperty("Image").GetString() != expectedImage ||
            labels.GetProperty("com.commutecast.owner").GetString() != "CommuteCast" ||
            labels.GetProperty("com.commutecast.contract").GetString() != "1" ||
            labels.GetProperty("com.docker.compose.project").GetString() != "commutecast" ||
            labels.GetProperty("com.docker.compose.service").GetString() != "whisper" ||
            ports.EnumerateObject().Count() != 1 ||
            !ports.TryGetProperty("8767/tcp", out var binding) || binding.GetArrayLength() != 1 ||
            binding[0].GetProperty("HostIp").GetString() != "127.0.0.1" ||
            binding[0].GetProperty("HostPort").GetString() != "8767")
            throw new IOException("Whisper container ownership, image or localhost binding does not match. Reprovision it using the setup script.");
    }

    private static async Task<string> DockerAsync(string[] args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("docker", args, TimeSpan.FromSeconds(15), ct);
        if (result.ExitCode != 0) throw new IOException("Docker could not verify or start local dictation.");
        return result.Output;
    }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var response = await http.PostAsync("transcribe", content, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new IOException("Whisper is finishing another recording. Try again shortly.");
        if (!response.IsSuccessStatusCode) throw new IOException("Whisper could not transcribe this recording. Check / repair dictation and try again.");
        var result = await response.Content.ReadFromJsonAsync<Transcript>(ct);
        return result?.Text?.Trim() ?? throw new IOException("Whisper returned an invalid transcript.");
    }

    public void Dispose() => http.Dispose();
    private sealed record Health(string Service, int Contract, string State, string Model, string Revision);
    private sealed record Transcript(string Text);
}
