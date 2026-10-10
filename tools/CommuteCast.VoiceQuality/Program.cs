using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

// Deliberately talks only to an explicitly supplied, isolated loopback trial service.
if (args.Length is < 3 or > 4 || args[0] is not ("kokoro" or "piper") ||
    !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/" ||
    args.Length == 4 && args[3] != "--long")
    throw new ArgumentException("Usage: CommuteCast.VoiceQuality kokoro|piper http://127.0.0.1:<trial-port> <new-output-folder> [--long]");
var engine = args[0]; var folder = Path.GetFullPath(args[2]);
if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Use a new comparison folder; existing evidence is preserved.");
var workspace = new Workspace(Path.Combine(folder, "workspace"));
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45)); var ct = deadline.Token;
using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
JsonElement health;
do { health = await http.GetFromJsonAsync<JsonElement>("health", ct); if (health.GetProperty("state").GetString() != "ready") await Task.Delay(500, ct); }
while (health.GetProperty("state").GetString() != "ready");
if (health.GetProperty("service").GetString() != "CommuteCast" || health.GetProperty("engine").GetString() != engine || health.GetProperty("localVoiceContract").GetInt32() != 1)
    throw new IOException("The trial service has an incompatible voice-quality contract.");
var fingerprint = health.GetProperty("fingerprint").GetString()!;
var voices = health.GetProperty("voices").EnumerateArray().Select(v => v.GetString()!).ToArray();
var voice = engine == "kokoro" ? "af_heart" : "en_US-lessac-medium";
var second = engine == "kokoro" ? "am_michael" : "en_US-amy-medium";
var tuned = engine == "kokoro" ? new LocalVoiceOptions(BlendVoice: "af_bella", BlendWeight: .25) : new LocalVoiceOptions(NoiseScale: .55, NoiseWidth: .65);
var scenarios = new List<(string Name, LocalVoiceOptions? Recipe)> { ("baseline", null), ("natural", new()), ("natural-repeat", new()), ("tuned", tuned) };
var results = new List<object>(); var audio = new AudioPipeline(new());

async Task Synthesize(NarrationSettings snapshot, string text, string path)
{
    var current = await http.GetFromJsonAsync<JsonElement>("health", ct);
    if (current.GetProperty("fingerprint").GetString() != fingerprint || current.GetProperty("active").GetInt32() != 0) throw new IOException("The trial service changed or is busy.");
    var identity = new { instance = current.GetProperty("instance").GetString(), sequence = current.GetProperty("sequence").GetInt64() + 1, fingerprint };
    using var reserve = await http.PostAsJsonAsync("reserve", identity, ct); reserve.EnsureSuccessStatusCode();
    try
    {
        var body = new Dictionary<string, object?> { ["instance"] = identity.instance, ["sequence"] = identity.sequence, ["fingerprint"] = fingerprint, ["text"] = text, ["voice"] = snapshot.Voice, ["speed"] = snapshot.Speed };
        if (snapshot.LocalVoice is not null) body["localVoice"] = snapshot.LocalVoice;
        using var response = await http.PostAsJsonAsync("speech", body, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "audio/wav") throw new IOException("Trial service returned non-WAV audio.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length is < 44 or > 64 * 1024 * 1024) throw new IOException("Invalid trial audio size.");
        await File.WriteAllBytesAsync(path, bytes, ct); WaveAudio.DataRegion(path, false);
    }
    finally { using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var settle = await http.PostAsJsonAsync("settle", identity, settlement.Token); settle.EnsureSuccessStatusCode(); }
}

async Task Run(string name, LocalVoiceOptions? recipe, bool podcast = false)
{
    var snapshot = new NarrationSettings(engine, voice, 1, false, "", fingerprint, LocalVoice: recipe);
    var job = new Job { Title = name, Source = AuditionRequest.ExpressiveSample, Settings = snapshot, Prepared = new(AuditionRequest.ExpressiveSample, []) };
    if (podcast)
    {
        job.Episode = new(PodcastDefaults.Formats[0], [new("Alex", "Host", "", "", voice, LocalVoice: new()), new("Casey", "Host", "", "", second, LocalVoice: new(GainDb: -2))]);
        job.Source = string.Join('\n', Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? "Alex: " : "Casey: ") + AuditionRequest.ExpressiveSample.Replace("\n\n", " ")));
        var prepared = PodcastScript.Prepare(job.Source, job.Episode, snapshot); job.Prepared = prepared.Prepared; job.Chunks = prepared.Units;
        job.ChunkingVersion = PodcastScript.ChunkVersion; job.AudioContractVersion = PodcastScript.AudioVersion;
    }
    NaturalChunker.ConfigureNew(job);
    if (job.Episode is null) job.Chunks = NaturalChunker.IsNatural(job) ? NaturalChunker.Split(job.Prepared.Script) : Chunker.Split(job.Prepared.Script, 450);
    var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory); var clock = Stopwatch.StartNew();
    foreach (var chunk in job.Chunks)
    {
        var settings = snapshot;
        if (job.Episode is not null) { var speaker = job.Episode.Speakers.Single(s => s.Name == chunk.Turns![0].Speaker); settings = snapshot with { Voice = speaker.Voice, LocalVoice = PodcastScript.LocalDelivery(speaker, snapshot) }; }
        var raw = Path.Combine(directory, "raw.wav"); await Synthesize(settings, chunk.Text, raw);
        var path = workspace.ChunkPath(job, chunk.Index); await audio.NormalizeAsync(job, raw, path, () => Task.CompletedTask, ct);
        var info = await audio.ValidateChunkAsync(path, chunk.Text, ct); job.Receipts.Add(new(chunk.Index, await Workspace.HashFileAsync(path, ct), job.Fingerprint, info.Duration)); File.Delete(raw);
    }
    var synthesisSeconds = clock.Elapsed.TotalSeconds;
    await audio.AssembleAsync(job, directory, ct); var final = workspace.FinalPath(job); var finalInfo = await audio.ValidateFinalAsync(job, final, ct);
    var target = Path.Combine(folder, name + ".mp3"); File.Copy(final, target);
    var loudness = await ProcessRunner.RunAsync("ffmpeg", ["-v", "info", "-nostats", "-i", target, "-af", "loudnorm=I=-19:TP=-2:LRA=11:print_format=json", "-f", "null", "-"], TimeSpan.FromMinutes(5), ct);
    if (loudness.ExitCode != 0) throw new IOException("Trial loudness analysis failed.");
    var start = loudness.Error.LastIndexOf('{'); var end = loudness.Error.LastIndexOf('}');
    var measurement = JsonSerializer.Deserialize<JsonElement>(loudness.Error[start..(end + 1)]);
    var result = new { name, engine, voice, fingerprint, recipe, chunks = job.Chunks.Count, normalizedPcmSha256 = job.Receipts.OrderBy(r => r.Index).Select(r => r.Hash).ToArray(), words = NarrationEstimate.CountWords(job.Prepared.Script), durationSeconds = finalInfo.Duration,
        synthesisSeconds, realTimeFactor = synthesisSeconds / finalInfo.Duration, totalSeconds = clock.Elapsed.TotalSeconds, sha256 = await Workspace.HashFileAsync(target, ct), file = Path.GetFileName(target), loudness = measurement };
    results.Add(result); await File.WriteAllTextAsync(Path.Combine(folder, "comparison.json"), JsonSerializer.Serialize(new { endpoint = endpoint.ToString(), results, listeningApproval = "pending" }, new JsonSerializerOptions { WriteIndented = true }), ct);
    Console.WriteLine($"{name}: {finalInfo.Duration:F1}s audio; synthesis {synthesisSeconds:F1}s; validated MP3 {target}");
}
foreach (var scenario in scenarios) await Run(scenario.Name, scenario.Recipe);
if (args.Length == 4) await Run("two-speaker-episode", new(), true);
