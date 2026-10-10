using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

/// <summary>Fixed-endpoint hosted adapters. A posted inference is never automatically repeated.</summary>
public sealed class HostedSpeechProvider(ISpeechSecrets secrets, HttpClient? client = null) : IDisposable
{
    private readonly HttpClient http = client ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly string[] OpenAiVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse", "marin", "cedar"];
    private static readonly string[] OpenAiLegacyVoices = ["alloy", "ash", "coral", "echo", "fable", "onyx", "nova", "sage", "shimmer"];
    private static readonly string[] GeminiVoices = ["Zephyr", "Puck", "Charon", "Kore", "Fenrir", "Leda", "Orus", "Aoede", "Callirrhoe", "Autonoe", "Enceladus", "Iapetus", "Umbriel", "Algieba", "Despina", "Erinome", "Algenib", "Rasalgethi", "Laomedeia", "Achernar", "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird", "Zubenelgenubi", "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat"];
    public Dictionary<string, string> VoiceNames { get; } = [];
    private HttpRequestMessage Request(string engine, HttpMethod method, string path, object? body = null)
    {
        var key = secrets.Get(engine) ?? throw new IOException($"Save an API key for {engine} in Speech providers before using hosted speech.");
        var host = engine switch { "openai" => "https://api.openai.com", "elevenlabs" => "https://api.elevenlabs.io", "cartesia" => "https://api.cartesia.ai", "gemini" => "https://generativelanguage.googleapis.com", _ => throw new ArgumentException("Unsupported hosted provider.") };
        var request = new HttpRequestMessage(method, host + path);
        if (engine == "elevenlabs") request.Headers.Add("xi-api-key", key);
        else if (engine == "gemini") request.Headers.Add("x-goog-api-key", key);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (engine == "cartesia") request.Headers.Add("Cartesia-Version", "2026-08-14");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new IOException("Provider response exceeds the supported audio size.");
        await using var input = await content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[81920];
        int count; while ((count = await input.ReadAsync(buffer, ct)) > 0) { if (output.Length + count > maximum) throw new IOException("Provider response exceeds the supported audio size."); await output.WriteAsync(buffer.AsMemory(0, count), ct); }
        return output.ToArray();
    }
    private static void EnsureSuccess(HttpResponseMessage response, string engine)
    {
        if (response.IsSuccessStatusCode) return;
        // Do not echo provider response bodies, which can contain source text or credential details.
        throw new IOException($"{engine} returned HTTP {(int)response.StatusCode}. " + (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "Check the API key and account permissions." : response.StatusCode == HttpStatusCode.TooManyRequests ? "Check quota or wait before retrying." : "Inspect the provider account before retrying; an inference may have been billed."));
    }
    public async Task<ProviderInfo> ReadyAsync(string engine, SpeechConfiguration config, CancellationToken ct)
    {
        config.Validate(engine); var voices = new List<string>();
        if (engine is "openai" or "gemini")
        {
            var path = engine == "openai" ? "/v1/models/" + Uri.EscapeDataString(config.Model) : "/v1beta/models/" + Uri.EscapeDataString(config.Model);
            using var request = Request(engine, HttpMethod.Get, path); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); EnsureSuccess(response, engine);
            using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 1024 * 1024, ct));
            voices.AddRange(engine == "openai" ? config.Model is "tts-1" or "tts-1-hd" ? OpenAiLegacyVoices : OpenAiVoices : GeminiVoices);
        }
        else
        {
            string? cursor = null;
            for (var page = 0; page < 10; page++)
            {
                var path = engine == "elevenlabs" ? "/v2/voices?page_size=100" + (cursor is null ? "" : "&next_page_token=" + Uri.EscapeDataString(cursor)) : "/voices?limit=100" + (cursor is null ? "" : "&starting_after=" + Uri.EscapeDataString(cursor));
                using var request = Request(engine, HttpMethod.Get, path); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); EnsureSuccess(response, engine);
                using var json = JsonDocument.Parse(await ReadBoundedAsync(response.Content, 4 * 1024 * 1024, ct)); var root = json.RootElement;
                foreach (var voice in root.GetProperty(engine == "elevenlabs" ? "voices" : "data").EnumerateArray())
                {
                    var id = voice.GetProperty(engine == "elevenlabs" ? "voice_id" : "id").GetString()!;
                    if (id.Length is < 1 or > 200) throw new IOException("Provider voice ID is invalid.");
                    voices.Add(id); VoiceNames[engine + ":" + id] = voice.TryGetProperty("name", out var name) ? name.GetString() ?? id : id;
                }
                if (!root.TryGetProperty("has_more", out var more) || !more.GetBoolean()) break;
                cursor = root.TryGetProperty(engine == "elevenlabs" ? "next_page_token" : "next_page", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : voices.LastOrDefault();
                if (string.IsNullOrEmpty(cursor)) throw new IOException("Provider voice pagination is incomplete.");
            }
        }
        if (voices.Count == 0) throw new IOException("This provider account has no available voices.");
        return new(engine, config.Identity(engine), voices.Distinct().ToArray(), "ready", 0);
    }
    public static void ValidateSettings(NarrationSettings settings)
    {
        var config = settings.Speech ?? throw new ArgumentException("Hosted speech needs a captured model."); config.Validate(settings.Engine);
        if (string.IsNullOrWhiteSpace(settings.Voice) || settings.Voice.Length > 200 || !double.IsFinite(settings.Speed) || settings.Speed is < .7 or > 1.4) throw new ArgumentException("Choose a valid hosted voice and pace.");
        if (settings.Engine == "elevenlabs" && settings.Speed > 1.2) throw new ArgumentException("ElevenLabs supports pace up to 1.2. Choose a lower pace.");
        if (settings.Engine == "gemini" && settings.Speed != 1) throw new ArgumentException("Gemini uses delivery instructions for pace. Set the pace slider to 1.0.");
    }
    public static (string Path, object Body, bool RawPcm) BuildRequest(NarrationSettings settings, string text, IReadOnlyList<PodcastTurn>? turns = null, PodcastEpisode? episode = null)
    {
        ValidateSettings(settings); var config = settings.Speech!;
        if (text.Length is < 1 || text.Length > SpeechProviders.Capabilities(settings.Engine, config.Model).MaximumCharacters) throw new ArgumentException("Speech block exceeds this provider's text limit.");
        string Tagged(string value, string emotion) => emotion.Length == 0 || emotion == "neutral" ? value : $"[{emotion}] {value}";
        switch (settings.Engine)
        {
            case "openai":
                var body = new Dictionary<string, object> { ["model"] = config.Model, ["input"] = text, ["voice"] = settings.Voice, ["speed"] = settings.Speed, ["response_format"] = "pcm" };
                if (config.Delivery.Length > 0) body["instructions"] = config.Delivery;
                return ("/v1/audio/speech", body, true);
            case "elevenlabs":
                if (turns?.Select(t => t.Speaker).Distinct().Count() > 1 && episode is not null)
                    return ("/v1/text-to-dialogue?output_format=pcm_24000", new { model_id = config.Model, inputs = turns.Select(t => { var speaker = episode.Speakers.Single(s => s.Name == t.Speaker); return new { text = Tagged(t.Text, speaker.Emotion), voice_id = speaker.Voice }; }).ToArray() }, true);
                return ("/v1/text-to-speech/" + Uri.EscapeDataString(settings.Voice) + "?output_format=pcm_24000", new { model_id = config.Model, text = Tagged(text, config.Emotion), voice_settings = new { speed = settings.Speed } }, true);
            case "cartesia":
                var generation = new Dictionary<string, object> { ["speed"] = settings.Speed }; if (config.Emotion.Length > 0) generation["emotion"] = config.Emotion;
                return ("/tts/bytes", new { model_id = config.Model, transcript = text, voice = settings.Voice, language = "en", generation_config = generation, output_format = new { container = "wav", encoding = "pcm_s16le", sample_rate = 24000 } }, false);
            case "gemini":
                object speech; object[] content;
                if (turns?.Select(t => t.Speaker).Distinct().Count() > 1 && episode is not null)
                {
                    speech = new { speakers = episode.Speakers.Select(s => new { speaker = s.Name, voice = s.Voice }).ToArray(), mode = "conversational" };
                    content = turns.Select(t => { var speaker = episode.Speakers.Single(s => s.Name == t.Speaker); return (object)new { type = "text", text = t.Text, annotations = new[] { new { type = "speech_metadata", speaker = t.Speaker, style = speaker.Delivery } } }; }).ToArray();
                }
                else { speech = new[] { new { voice = settings.Voice } }; content = [new { type = "text", text, annotations = new[] { new { type = "speech_metadata", style = config.Delivery } } }]; }
                return ("/v1beta/interactions", new { model = config.Model, input = new[] { new { type = "user_input", content } }, response_format = new { type = "audio", mime_type = "audio/l16" }, generation_config = new { speech_config = speech } }, true);
            default: throw new ArgumentException("Unsupported provider.");
        }
    }
    public async Task WriteAsync(Job job, NarrationSettings settings, string text, Stream output, Func<Task> checkpoint, CancellationToken ct, IReadOnlyList<PodcastTurn>? turns = null)
    {
        var spec = BuildRequest(settings, text, turns, job.Episode);
        using var request = Request(settings.Engine, HttpMethod.Post, spec.Path, spec.Body);
        var attempt = new SpeechAttempt(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, settings.Engine, settings.Speech!.Model, Job.Hash(text), "started", UnitIndex: job.Chunks.FirstOrDefault(c => c.Text == text && !job.Receipts.Any(r => r.Index == c.Index && r.Fingerprint == job.Fingerprint))?.Index);
        job.SpeechAttempts ??= []; job.SpeechAttempts.Add(attempt); await checkpoint();
        async Task Record(string state, string? requestId = null, string? usage = null, DateTimeOffset? retryAfter = null)
        {
            job.SpeechAttempts[job.SpeechAttempts.FindIndex(a => a.Id == attempt.Id)] = attempt with { State = state, RequestId = requestId, Usage = usage, RetryAfterUtc = retryAfter }; await checkpoint();
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var requestId = new[] { "x-request-id", "request-id" }.Select(h => response.Headers.TryGetValues(h, out var values) ? values.FirstOrDefault() : null).FirstOrDefault(v => v is not null);
            if (requestId?.Length > 200 || requestId?.Any(char.IsControl) == true) requestId = null;
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter?.Date;
                if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromDays(1)) retryAfter = DateTimeOffset.UtcNow + delta;
                await Record((int)response.StatusCode is >= 400 and < 500 ? "rejected" : "uncertain", requestId, retryAfter: retryAfter); EnsureSuccess(response, settings.Engine);
            }
            var bytes = await ReadBoundedAsync(response.Content, 64 * 1024 * 1024, ct); string? usage = null;
            if (settings.Engine != "gemini" && response.Content.Headers.ContentType?.MediaType is { } mime &&
                (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The provider returned non-audio content. Inspect provider usage before retrying.");
            if (settings.Engine == "gemini")
            {
                using var json = JsonDocument.Parse(bytes);
                var audio = json.RootElement.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("type").GetString() == "model_output").SelectMany(s => s.GetProperty("content").EnumerateArray()).Where(c => c.GetProperty("type").GetString() == "audio").ToArray();
                if (audio.Length != 1 || audio[0].GetProperty("mime_type").GetString() != "audio/l16") throw new IOException("Gemini did not return one PCM audio block. Inspect usage before retrying.");
                bytes = Convert.FromBase64String(audio[0].GetProperty("data").GetString()!);
                if (json.RootElement.TryGetProperty("usage", out var values)) usage = JsonSerializer.Serialize(values.EnumerateObject().Where(p => p.Name is "total_input_tokens" or "total_output_tokens" or "total_tokens" && p.Value.ValueKind == JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.Clone()));
            }
            if (spec.RawPcm)
            {
                if (bytes.Length == 0 || bytes.Length % 2 != 0) throw new IOException("Hosted PCM audio is empty or truncated.");
                if (bytes.AsSpan().StartsWith("RIFF"u8) || bytes.AsSpan().StartsWith("ID3"u8)) throw new IOException("Provider audio container differs from the requested raw PCM contract.");
                WaveAudio.WriteHeader(output, bytes.Length / 2);
            }
            else { using var wav = new MemoryStream(bytes); WaveAudio.DataRegion(wav, false); }
            await output.WriteAsync(bytes, ct); await Record("received", requestId, usage);
        }
        catch (Exception error)
        {
            if (job.SpeechAttempts.Single(a => a.Id == attempt.Id).State == "started")
            { try { await Record("uncertain"); } catch { /* the pre-request durable intent still identifies uncertain billing after recovery */ } }
            if (error is OperationCanceledException && !ct.IsCancellationRequested) throw new TimeoutException("The hosted speech request timed out and may have been billed. Inspect provider usage before explicitly retrying.", error);
            throw;
        }
    }
    public void Dispose() { if (client is null) http.Dispose(); }
}
