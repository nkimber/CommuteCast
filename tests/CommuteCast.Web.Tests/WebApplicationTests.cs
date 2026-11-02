using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommuteCast.Core;
using CommuteCast.Infrastructure;
using CommuteCast.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CommuteCast.Web.Tests;

public sealed class WebApplicationTests
{
    [Fact]
    public async Task RendersStudioAndNeverExposesAudioOrWorkspaceFiles()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var html = await client.GetStringAsync("/");
        Assert.Contains("Make the most", html); Assert.Contains("csrf-token", html);
        Assert.DoesNotContain("<audio", html); Assert.DoesNotContain("<video", html);
        foreach (var path in new[] { "/complete.mp3", "/jobs/123/complete.mp3", "/api/jobs/123/audio", "/api/jobs/123/download", "/workspace/settings.json", "/queue.db" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        var response = await client.GetAsync("/api/state");
        Assert.Contains("media-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task LocalModeRejectsRemoteAddressesAndPublicHostEvenWithForgedForwardingHeaders()
    {
        await using var host = new StudioFactory(remoteAddress: IPAddress.Parse("192.0.2.5")); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1"); client.DefaultRequestHeaders.Add("Cf-Access-Authenticated-User-Email", "owner@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        await using var loopback = new StudioFactory(); using var publicClient = loopback.CreateClient();
        publicClient.DefaultRequestHeaders.Host = "commutecast.kimber.dev";
        Assert.Equal(HttpStatusCode.Unauthorized, (await publicClient.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task StateChangesRequireAntiforgeryToken()
    {
        await using var host = new StudioFactory(); using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/state"); Assert.False(state.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task DraftRevisionPreventsAnotherBrowserOverwritingNewerText()
    {
        await using var host = new StudioFactory(); using var first = await host.OwnerClientAsync(); using var second = await host.OwnerClientAsync();
        var original = await first.GetFromJsonAsync<JsonElement>("/api/draft"); var revision = original.GetProperty("revision").GetString()!;
        Assert.True((await first.PostAsJsonAsync("/api/draft", new DraftRequest(new Draft("First", "The latest words."), revision))).IsSuccessStatusCode);
        var conflict = await second.PostAsJsonAsync("/api/draft", new DraftRequest(new Draft("Stale", "Older words."), revision));
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        var retained = await first.GetFromJsonAsync<JsonElement>("/api/draft");
        Assert.Equal("The latest words.", retained.GetProperty("draft").GetProperty("source").GetString());
    }

    [Fact]
    public async Task PromptAndPreviewUseProductionPreparationWithoutInvokingSpeech()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var prompt = await client.PostAsJsonAsync("/api/prompt", new PromptRequest(new() { Topic = "How cities work", Minutes = "10" }));
        prompt.EnsureSuccessStatusCode(); Assert.Contains("How cities work", await prompt.Content.ReadAsStringAsync());
        var preview = await client.PostAsJsonAsync("/api/preview", Request("# A useful idea\n\nA thoughtful explanation.")); preview.EnsureSuccessStatusCode();
        var content = await preview.Content.ReadFromJsonAsync<JsonElement>(); Assert.DoesNotContain("#", content.GetProperty("script").GetString());
        Assert.Equal(0, host.Speech.Calls);
    }

    [Fact]
    public async Task SubmissionIsIdempotentAndGenerationSurvivesBrowserDisconnect()
    {
        await using var host = new StudioFactory(); var client = await host.OwnerClientAsync();
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
        var request = Request("A good idea changes the way we notice the world. A familiar journey becomes time well spent.");
        (await client.PostAsJsonAsync("/api/jobs", request)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/jobs", request)).EnsureSuccessStatusCode();
        Assert.Single(host.Services.GetRequiredService<WebRuntime>().Queue.Snapshot());
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(false))).EnsureSuccessStatusCode(); client.Dispose();
        var runtime = host.Services.GetRequiredService<WebRuntime>();
        var timeout = DateTime.UtcNow.AddSeconds(45);
        Job? job;
        do { await Task.Delay(100); job = runtime.Queue.Snapshot().Single(); } while (job.Stage is not (JobStage.Exported or JobStage.Failed) && DateTime.UtcNow < timeout);
        Assert.True(job.Stage == JobStage.Exported, job.Error);
        Assert.True(File.Exists(runtime.Workspace.FinalPath(job)));
        Assert.Equal(1, host.Speech.Calls);
        using var reconnect = host.CreateClient(); Assert.Equal(HttpStatusCode.OK, (await reconnect.GetAsync("/api/jobs/" + job.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reconnect.GetAsync("/api/jobs/" + job.Id + "/download")).StatusCode);
    }

    [Fact]
    public async Task QueueAndPublicationIntentSurviveHostRestart()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CommuteCast-Web-tests-" + Guid.NewGuid().ToString("N"));
        string id;
        await using (var first = new StudioFactory(parent))
        {
            using var client = await first.OwnerClientAsync(); (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
            var request = Request("A durable narration waits for its turn."); id = request.SubmissionId!;
            (await client.PostAsJsonAsync("/api/jobs", request)).EnsureSuccessStatusCode();
        }
        await using (var second = new StudioFactory(parent))
        {
            using var client = await second.OwnerClientAsync(); var runtime = second.Services.GetRequiredService<WebRuntime>();
            Assert.True(runtime.Queue.Paused); Assert.Equal(id, Assert.Single(runtime.Queue.Snapshot()).Id);
            var details = await client.GetFromJsonAsync<JsonElement>("/api/jobs/" + id);
            Assert.Equal("Pending", details.GetProperty("publication").GetProperty("status").GetString());
        }
        Directory.Delete(parent, true);
    }

    [Theory]
    [InlineData("owner@example.com", "correct-audience", false, true)]
    [InlineData("someone@example.com", "correct-audience", false, false)]
    [InlineData("owner@example.com", "wrong-audience", false, false)]
    [InlineData("owner@example.com", "correct-audience", true, false)]
    public async Task CloudflareModeValidatesSignatureAudienceExpiryAndOwner(string email, string audience, bool expired, bool accepted)
    {
        await using var host = new StudioFactory(cloudflare: true); using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", host.Token(email, audience, expired));
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
    }

    private static ComposeRequest Request(string text) => new("Test narration", text,
        new("kokoro", "af_heart", 1, false, "", new(), LocalVoice: null), SubmissionId: Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedDefaultSaveCannotLeakUnsavedChoicesIntoALaterSettingsWrite()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var runtime = host.Services.GetRequiredService<WebRuntime>();
        var settingsPath = Path.Combine(runtime.Workspace.Root, "settings.json");
        Directory.CreateDirectory(settingsPath);
        var response = await client.PostAsJsonAsync("/api/defaults", Request("text").Options with { Speed = 1.2 });
        Assert.False(response.IsSuccessStatusCode);
        Directory.Delete(settingsPath);
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
        var persisted = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsPath))!;
        Assert.Equal(1, persisted.NarrationDefaults!.Speed);
        Assert.Equal(1, runtime.Settings.Speed);
    }

    [Fact]
    public async Task CheckingAnotherHostedModelPreservesSavedDefaultsAcrossRestart()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CommuteCast-Web-tests-" + Guid.NewGuid().ToString("N"));
        var saved = new NarrationOptions("openai", "coral", 1, false, "", new(), Speech: new("tts-1"));
        await using (var host = new StudioFactory(parent))
        {
            using var client = await host.OwnerClientAsync();
            (await client.PostAsJsonAsync("/api/defaults", saved)).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/providers/check", new ProviderRequest("openai", "tts-1-hd"))).EnsureSuccessStatusCode();
            var state = await client.GetFromJsonAsync<JsonElement>("/api/state");
            Assert.Equal("tts-1", state.GetProperty("defaults").GetProperty("speech").GetProperty("model").GetString());
        }
        await using (var restarted = new StudioFactory(parent))
        {
            using var client = await restarted.OwnerClientAsync();
            var state = await client.GetFromJsonAsync<JsonElement>("/api/state");
            Assert.Equal("tts-1", state.GetProperty("defaults").GetProperty("speech").GetProperty("model").GetString());
            Assert.Equal("coral", state.GetProperty("defaults").GetProperty("voice").GetString());
        }
        Directory.Delete(parent, true);
    }

    [Fact]
    public async Task SpeechRepairTimeoutPreservesActionableGuidanceAndReleasesSetupGate()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        host.Speech.RepairUnavailable = true;
        var response = await client.PostAsJsonAsync("/api/providers/repair", new ProviderRequest("piper"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Open Docker Desktop", await response.Content.ReadAsStringAsync());
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ConcurrentShutdownCallsBothWaitForWorkspaceLeaseRelease()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var runtime = host.Services.GetRequiredService<WebRuntime>();
        (await client.PostAsJsonAsync("/api/jobs", Request("A narration can be interrupted by a controlled server shutdown."))).EnsureSuccessStatusCode();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Speech.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(1, host.Speech.Calls);
        await Task.WhenAll(runtime.StopAsync(default), runtime.StopAsync(default));
        using var lease = WorkspaceLease.Acquire(runtime.Workspace);
        lease.EnsureHeld();
    }

    [Fact]
    public async Task DraftStoresSourceAndVoiceSettingsTogether()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var loaded = await client.GetFromJsonAsync<JsonElement>("/api/draft");
        var options = Request("text").Options with { Speed = 1.15 };
        (await client.PostAsJsonAsync("/api/draft", new DraftRequest(new("A saved draft", "My reading text."), loaded.GetProperty("revision").GetString()!, options))).EnsureSuccessStatusCode();
        var saved = await client.GetFromJsonAsync<JsonElement>("/api/draft");
        Assert.Equal(1.15, saved.GetProperty("options").GetProperty("speed").GetDouble());
        Assert.Equal("My reading text.", saved.GetProperty("draft").GetProperty("source").GetString());
    }

    [Fact]
    public async Task DuplicateSubmissionWithChangedVoiceSettingsIsRejected()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
        var request = Request("One saved narration."); (await client.PostAsJsonAsync("/api/jobs", request)).EnsureSuccessStatusCode();
        var conflict = await client.PostAsJsonAsync("/api/jobs", request with { Options = request.Options with { Speed = 1.1 } });
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Equal(1, Assert.Single(host.Services.GetRequiredService<WebRuntime>().Queue.Snapshot()).Settings.Speed);
    }

    [Fact]
    public async Task PublishingRetryReusesAudioAndRetainsCapturedDestination()
    {
        await using var host = new StudioFactory(); using var client = await host.OwnerClientAsync();
        var destination = new M365Destination(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "original-drive", "original-folder");
        (await client.PostAsJsonAsync("/api/destination", new DestinationRequest(destination, true))).EnsureSuccessStatusCode();
        host.Publishing.FailNext = true;
        var request = Request("An interesting explanation for the journey home.");
        (await client.PostAsJsonAsync("/api/jobs", request)).EnsureSuccessStatusCode();
        await WaitPublicationAsync(client, request.SubmissionId!, "Failed");
        var changed = destination with { DriveId = "new-drive", FolderId = "new-folder" };
        (await client.PostAsJsonAsync("/api/destination", new DestinationRequest(changed, true))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/jobs/" + request.SubmissionId + "/publish", new PublicationRequest())).EnsureSuccessStatusCode();
        await WaitPublicationAsync(client, request.SubmissionId!, "Published");
        Assert.Equal(1, host.Speech.Calls); Assert.Equal(2, host.Publishing.Attempts.Count);
        Assert.All(host.Publishing.Attempts, attempted => Assert.Equal(destination, attempted));
    }

    [Fact]
    public async Task CloudflareOwnerCanObtainAndUseBrowserAntiforgeryToken()
    {
        await using var host = new StudioFactory(cloudflare: true); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Cf-Access-Jwt-Assertion", host.Token("owner@example.com", "correct-audience", false));
        var html = await client.GetStringAsync("/");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value);
        (await client.PostAsJsonAsync("/api/queue", new QueueRequest(true))).EnsureSuccessStatusCode();
        Assert.True(host.Services.GetRequiredService<WebRuntime>().Queue.Paused);
    }

    private static async Task WaitPublicationAsync(HttpClient client, string id, string wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var details = await client.GetFromJsonAsync<JsonElement>("/api/jobs/" + id);
            if (details.GetProperty("publication").GetProperty("status").GetString() == wanted) return;
            if (details.GetProperty("stage").GetString() == "Failed") Assert.Fail(details.GetProperty("error").GetString());
            await Task.Delay(150);
        }
        Assert.Fail("Publication did not reach " + wanted);
    }

    private sealed class StudioFactory : WebApplicationFactory<Program>
    {
        private readonly string parent;
        private readonly bool ownsParent, cloudflare;
        private readonly IPAddress remote;
        private readonly RSA rsa = RSA.Create(2048);
        public SyntheticSpeech Speech { get; } = new();
        public SyntheticPublishing Publishing { get; } = new();
        public StudioFactory(string? parent = null, IPAddress? remoteAddress = null, bool cloudflare = false)
        {
            ownsParent = parent is null; this.parent = parent ?? Path.Combine(Path.GetTempPath(), "CommuteCast-Web-tests-" + Guid.NewGuid().ToString("N"));
            remote = remoteAddress ?? IPAddress.Loopback; this.cloudflare = cloudflare;
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CommuteCast:Workspace"] = Path.Combine(parent, "private"), ["CommuteCast:Exports"] = Path.Combine(parent, "exports"),
                ["CommuteCast:OperationalLogging"] = "false",
                ["Cloudflare:TeamDomain"] = cloudflare ? "test.cloudflareaccess.com" : "", ["Cloudflare:Audience"] = "correct-audience", ["Cloudflare:AllowedEmail"] = "owner@example.com"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new ConnectionAddress(remote));
                services.AddSingleton<IWebSpeechFactory>(new SyntheticFactory(Speech));
                services.AddSingleton<IM365Publisher>(Publishing);
                if (cloudflare) services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = "https://test.cloudflareaccess.com" };
                    config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
            });
        }
        public string Token(string email, string audience, bool expired) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://test.cloudflareaccess.com", Audience = audience, Subject = new ClaimsIdentity([new Claim("email", email)]),
            IssuedAt = DateTime.UtcNow.AddHours(-2), NotBefore = DateTime.UtcNow.AddHours(-2), Expires = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256)
        });
        public async Task<HttpClient> OwnerClientAsync()
        {
            var client = CreateClient(); var html = await client.GetStringAsync("/");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]+)\"").Groups[1].Value); return client;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync(); rsa.Dispose();
            if (ownsParent && Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }
    private sealed class ConnectionAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, after) => { context.Connection.RemoteIpAddress = address; await after(); }); next(app);
        };
    }
    private sealed class SyntheticFactory(SyntheticSpeech speech) : IWebSpeechFactory
    {
        public ISpeechService Create(Workspace workspace, AppSettings settings, ISpeechSecrets secrets) => speech;
    }
    public sealed class SyntheticSpeech : ISpeechService
    {
        public int Calls;
        public bool RepairUnavailable;
        public IReadOnlyDictionary<string, string> VoiceNames { get; } = new Dictionary<string, string>();
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => Task.FromResult(new ProviderInfo(engine, "web-test", ["af_heart"], "ready", 0));
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct, bool explicitRetry) => ReadyAsync(engine, ct);
        public Task<ProviderInfo> CaptureForSubmissionAsync(string engine, ProviderInfo? cached, CancellationToken ct = default) => ReadyAsync(engine, ct);
        public async Task<SpeechRepairResult> RepairAsync(string engine, CancellationToken ct = default)
        {
            if (RepairUnavailable) throw new TimeoutException("Open Docker Desktop and wait for local services to become available, then retry.");
            return new(await ReadyAsync(engine, ct), "Synthetic installed services checked.");
        }
        public Task<ProviderInfo> ReadyForJobAsync(Job job, CancellationToken ct) => ReadyAsync(job.Settings.Engine, ct);
        public Task<ProviderInfo> ReadyForSettingsAsync(NarrationSettings settings, CancellationToken ct) => ReadyAsync(settings.Engine, ct);
        public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); await Task.Delay(150, ct);
            using var file = File.Create(output); const int samples = 24000 * 4; WaveAudio.WriteHeader(file, samples);
            using var writer = new BinaryWriter(file); for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
        }
        public async Task SynthesizeAsync(Job job, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct)
        {
            await SynthesizeAsync(settings, text, output, ct); await PrivateJobFiles.RecordAsync(job, Path.GetDirectoryName(output)!, Path.GetFileName(output), ct); await checkpoint();
        }
        public Task SynthesizeUnitAsync(Job job, TextChunk unit, string output, Func<Task> checkpoint, CancellationToken ct) => SynthesizeAsync(job, job.Settings, unit.Text, output, checkpoint, ct);
        public void Dispose() { }
    }
    public sealed class SyntheticPublishing : IM365Publisher
    {
        public bool FailNext;
        public List<M365Destination> Attempts { get; } = [];
        public Task<M365Folder> CheckFolderAsync(M365Destination destination, bool createPublicLink, CancellationToken ct) => Task.FromResult(new M365Folder("Test folder", "https://tenant.sharepoint.com/public-folder"));
        public Task<M365Upload> UploadAsync(M365Destination destination, string path, string name, string expectedHash, CancellationToken ct)
        {
            Attempts.Add(destination);
            if (FailNext) { FailNext = false; throw new IOException("Synthetic Microsoft 365 outage."); }
            return Task.FromResult(new M365Upload("test-item", name, "test-etag", new FileInfo(path).Length));
        }
    }
}
