using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Net;
using System.Reflection;
using System.Text.Json;

// Exercises the actual adapter and SQLite ownership journal. All Docker/HTTP
// observations are synthetic, in process; this host never contacts a daemon.
internal static class SpeechWriteBoundary
{
    private const string Image = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Created = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Source = "Preserve this frozen synthetic source.";
    internal static async Task RunAsync(string[] args, Workspace workspace, string marker)
    {
        var engine = args[2]; var store = new SqliteJobStore(workspace);
        var settings = new NarrationSettings(engine, engine == "kokoro" ? "af_heart" : "en_US-lessac-medium", 1, false, "", engine + ":contract-v1:" + new string('c', 64), ProviderImageId: Image);
        var build = typeof(Job).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        using var waveStream = new MemoryStream(); WaveAudio.WriteHeader(waveStream, 48000);
        using (var writer = new BinaryWriter(waveStream, System.Text.Encoding.UTF8, true))
            for (var i = 0; i < 48000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
        var wave = waveStream.ToArray(); var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave));
        var http = new SyntheticHttp(settings, wave);
        using var provider = new LocalSpeechProvider(workspace, new SyntheticRuntime(engine), http);
        if (args[0] == "speech-write-barrier")
        {
            if ((await store.LoadAsync()).Count != 0 || File.Exists(marker) || Directory.Exists(marker)) throw new IOException("Use a fresh speech-write fixture.");
            await using (var pin = new FileStream(Path.Combine(workspace.Root, "provider-lock.local.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(pin, new { ImageId = Image, Contract = 1 });
            var job = new Job { Title = "Synthetic speech download", Source = Source, CreatedUtc = Created, Settings = settings, Stage = JobStage.Failed };
            var directory = workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown.txt"), "Preserve unrelated bytes.");
            await store.SaveAsync(job);
            async Task BarrierAsync(string point)
            {
                if (point != args[3]) return;
                var saved = (await store.LoadAsync()).Single();
                var existing = saved.PrivateArtifacts.Select(r => r.RelativePath).Where(name => File.Exists(Path.Combine(directory, name))).ToArray();
                if (existing.Length != 1) throw new IOException("Expected precisely one recorded live speech output.");
                await Workspace.AtomicWriteAsync(marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId, applicationBuild = build, point, engine, job.Id, expectedHash = hash, surviving = existing[0], observedBytes = new FileInfo(Path.Combine(directory, existing[0])).Length, reservationPresent = File.Exists(Path.Combine(workspace.Root, "speech-admission.json")) }));
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            http.Body = () => new BoundaryBody(wave, () => BarrierAsync("during-copy"));
            var saves = 0;
            await provider.SynthesizeAsync(job, settings, "Synthetic spoken text.", Path.Combine(directory, "inference.partial.wav"), async () =>
            {
                saves++;
                if (saves == 3) await BarrierAsync("after-rename"); // last durable state still names both sides
                await store.SaveAsync(job);
                await BarrierAsync(saves switch { 1 => "before-copy", 2 => "before-rename", _ => "after-complete-save" });
            }, default);
            throw new IOException("The speech boundary was not reached.");
        }
        var savedJob = (await store.LoadAsync()).Single(); var boundary = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
        using (boundary)
        {
            var point = boundary.RootElement.GetProperty("point").GetString();
            if (point is not ("before-copy" or "during-copy" or "before-rename" or "after-rename" or "after-complete-save") ||
                boundary.RootElement.GetProperty("engine").GetString() != engine || boundary.RootElement.GetProperty("Id").GetString() != savedJob.Id ||
                boundary.RootElement.GetProperty("expectedHash").GetString() != hash || boundary.RootElement.GetProperty("applicationBuild").GetString() != build)
                throw new IOException("The observed boundary does not identify this fixture and build.");
            if (savedJob.Source != Source || savedJob.CreatedUtc != Created || savedJob.Settings != settings || savedJob.Title != "Synthetic speech download") throw new IOException("Frozen job identity changed.");
            var root = workspace.JobDirectory(savedJob.Id); var surviving = boundary.RootElement.GetProperty("surviving").GetString()!;
            var survivingPath = OwnedFileRemoval.Resolve(root, surviving);
            if (!savedJob.PrivateArtifacts.Any(r => r.RelativePath.Equals(surviving, StringComparison.Ordinal))) throw new IOException("Observed output has no durable receipt.");
            var incomplete = point is "before-copy" or "during-copy";
            if (incomplete != savedJob.PrivateArtifacts.Any(r => r.CreationIdentity is not null)) throw new IOException("Durable creation state does not match the observed boundary.");
            if (!incomplete && await Workspace.HashFileAsync(survivingPath) != hash) throw new IOException("Completed surviving bytes changed.");
            await PrivateJobFiles.ReconcilePromotionsAsync(savedJob, root, () => store.SaveAsync(savedJob), default);
            if (incomplete && File.Exists(survivingPath)) throw new IOException("Original incomplete attempt survived recovery.");
            if (savedJob.PrivateArtifacts.Any(r => r.CreationIdentity is not null || r.PromotionIdentity is not null)) throw new IOException("Ownership intents did not settle.");
            foreach (var receipt in savedJob.PrivateArtifacts.Where(r => r.RelativePath.Contains(".attempt-", StringComparison.Ordinal)).ToArray())
                await PrivateJobFiles.PrepareOutputAsync(savedJob, root, receipt.RelativePath, default);
            await store.SaveAsync(savedJob);
            await provider.SynthesizeAsync(savedJob, settings, "Synthetic retry text.", Path.Combine(root, "inference.partial.wav"), () => store.SaveAsync(savedJob), default);
            var reopened = (await store.LoadAsync()).Single();
            if (PrivateJobFiles.Inventory(reopened)["inference.partial.wav"] != hash || await Workspace.HashFileAsync(Path.Combine(root, "inference.partial.wav")) != hash ||
                await File.ReadAllTextAsync(Path.Combine(root, "unknown.txt")) != "Preserve unrelated bytes." || File.Exists(Path.Combine(workspace.Root, "speech-admission.json")) ||
                Directory.GetFiles(root, "*.attempt-*.partial").Length != 0 || reopened.Source != Source || reopened.Settings != settings || reopened.CreatedUtc != Created)
                throw new IOException("Recovered retry, reservation retirement or unrelated preservation failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { applicationBuild = build, savedJob.Id, engine, point, expectedHash = hash, exactRecovery = true, durableRetry = true, unrelatedPreserved = true, frozenJobPreserved = true, reservationSettled = true }));
        }
    }

    private sealed class BoundaryBody(byte[] wave, Func<Task> barrier) : MemoryStream(wave)
    {
        private int reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (++reads == 2) await barrier();
            return await base.ReadAsync(buffer, ct);
        }
    }
    private sealed class SyntheticHttp(NarrationSettings settings, byte[] wave) : HttpMessageHandler
    {
        private long sequence; private readonly string instance = new('d', 32);
        public Func<Stream>? Body { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1" } uri || uri.Port != (settings.Engine == "kokoro" ? 8765 : 8766)) throw new IOException("Unexpected synthetic HTTP scope.");
            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/health") return Json(new { service = "CommuteCast", contract = 1, engine = settings.Engine, fingerprint = settings.ProviderFingerprint, voices = new[] { settings.Voice }, state = "ready", active = 0, admission = 1, instance, sequence });
            if (request.Method != HttpMethod.Post) throw new IOException("Unexpected synthetic HTTP method.");
            if (uri.AbsolutePath is "/reserve" or "/settle")
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                sequence = Math.Max(sequence, document.RootElement.GetProperty("sequence").GetInt64());
                return Json(new { service = "CommuteCast", contract = 1, engine = settings.Engine, instance, sequence, state = uri.AbsolutePath == "/reserve" ? "reserved" : "settled" });
            }
            if (uri.AbsolutePath != "/speech") throw new IOException("Unexpected synthetic HTTP route.");
            var content = new StreamContent(Body?.Invoke() ?? new MemoryStream(wave)); content.Headers.ContentType = new("audio/wav");
            return new(HttpStatusCode.OK) { Content = content };
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    }
    private sealed class SyntheticRuntime(string engine) : ILocalSpeechRuntime
    {
        public Task<ProcessResult> DockerAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var output = args[0] switch
            {
                "context" => "npipe:////./pipe/dockerDesktopLinuxEngine",
                "version" => "29.2.1",
                "inspect" when args.Count == 2 && args[1] == "commutecast-" + engine => JsonSerializer.Serialize(new[] { new {
                    Name = "/commutecast-" + engine, Image,
                    Config = new { Labels = new Dictionary<string, string> { ["com.commutecast.owner"] = "CommuteCast", ["com.docker.compose.project"] = "commutecast", ["com.commutecast.contract"] = "1" }, Cmd = new[] { "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log" }, Entrypoint = (string[]?)null, Env = new[] { "COMMUTECAST_ENGINE=" + engine } },
                    HostConfig = new { PortBindings = new Dictionary<string, object> { ["8765/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = engine == "kokoro" ? "8765" : "8766" } } }, Privileged = false, NetworkMode = "bridge", NanoCpus = 2_000_000_000L, Memory = (engine == "kokoro" ? 2L : 1L) * 1024 * 1024 * 1024, SecurityOpt = new[] { "no-new-privileges:true" } },
                    Mounts = Array.Empty<object>(), State = new { Running = true, OOMKilled = false, Paused = false, Restarting = false }
                } }),
                _ => throw new IOException("The synthetic runtime refuses mutation or unexpected commands.")
            };
            return Task.FromResult(new ProcessResult(0, output, ""));
        }
        public void LaunchInstalledDesktop() => throw new IOException("The synthetic runtime cannot launch Docker Desktop.");
    }
}
