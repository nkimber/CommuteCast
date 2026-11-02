using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class M365PublishingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "commutecast-m365-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly M365Destination Destination = new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "drive", "folder");
    private readonly byte[] bytes = [1, 2, 3, 4, 5];
    private string Hash => Convert.ToHexString(SHA256.HashData(bytes));
    public M365PublishingTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private async Task<string> AudioAsync() { var path = Path.Combine(root, "test.mp3"); await File.WriteAllBytesAsync(path, bytes); return path; }
    private static HttpResponseMessage Json(object data, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(data) };
    private static HttpResponseMessage Token() => Json(new { access_token = "test-token" });
    private object Receipt() => new { id = "remote-item", name = "test.mp3", size = bytes.Length, eTag = "etag", file = new { } };

    [Fact]
    public async Task UploadVerifiesBytesAndKeepsBearerOffPreauthenticatedUpload()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            var url = request.RequestUri!.AbsoluteUri; calls.Add(request.Method + " " + url);
            if (url.Contains("oauth2")) return Token();
            if (url.Contains("createUploadSession"))
            {
                Assert.Contains("fail", await request.Content!.ReadAsStringAsync());
                return Json(new { uploadUrl = "https://tenant.sharepoint.com/upload?secret=private" });
            }
            if (request.Method == HttpMethod.Put)
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync());
                Assert.Equal("bytes 0-4/5", request.Content.Headers.ContentRange!.ToString());
                return Json(Receipt(), HttpStatusCode.Created);
            }
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            return url.EndsWith("/content") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound);
        }));
        var receipt = await new M365Publisher(http, _ => "secret").UploadAsync(Destination, await AudioAsync(), "test.mp3", Hash, default);
        Assert.Equal("remote-item", receipt.ItemId);
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public async Task LostResponseRecoveryVerifiesExistingFileWithoutAnotherUpload()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post == request.Method, request.RequestUri!.Host == "login.microsoftonline.com");
            return Task.FromResult(request.Method == HttpMethod.Post ? Token() : request.RequestUri!.AbsolutePath.EndsWith("/content")
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : Json(Receipt()));
        }));
        Assert.Equal("remote-item", (await new M365Publisher(http, _ => "secret").UploadAsync(Destination, await AudioAsync(), "test.mp3", Hash, default)).ItemId);
    }

    [Fact]
    public async Task ExistingDifferentBytesArePreserved()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.Method == HttpMethod.Post ? Token()
            : request.RequestUri!.AbsolutePath.EndsWith("/content") ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) } : Json(Receipt()))));
        var error = await Assert.ThrowsAsync<IOException>(() => new M365Publisher(http, _ => "secret").UploadAsync(Destination, AudioAsync().Result, "test.mp3", Hash, default));
        Assert.Contains("differs", error.Message);
    }

    [Fact]
    public async Task ChangedLocalFileNeverContactsMicrosoft()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected network access")));
        await Assert.ThrowsAsync<IOException>(() => new M365Publisher(http, _ => "secret").UploadAsync(Destination, AudioAsync().Result, "test.mp3", "changed", default));
    }

    [Fact]
    public async Task RefusesUntrustedUploadHost()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.Host == "login.microsoftonline.com" ? Token()
            : request.Method == HttpMethod.Get ? new(HttpStatusCode.NotFound) : Json(new { uploadUrl = "https://attacker.example/upload" }))));
        var error = await Assert.ThrowsAsync<IOException>(() => new M365Publisher(http, _ => "secret").UploadAsync(Destination, AudioAsync().Result, "test.mp3", Hash, default));
        Assert.Contains("unsupported upload host", error.Message);
    }

    [Fact]
    public async Task OnlyReadOnlyAnonymousFolderLinkQualifies()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(request.RequestUri!.Host == "login.microsoftonline.com" ? Token()
            : request.RequestUri!.AbsolutePath.EndsWith("/permissions") ? Json(new { value = new[] {
                new { link = new { scope = "anonymous", type = "edit", webUrl = "https://tenant.sharepoint.com/edit" } },
                new { link = new { scope = "anonymous", type = "view", webUrl = "https://tenant.sharepoint.com/view" } } } })
            : Json(new { name = "CommuteCast", folder = new { } }))));
        var folder = await new M365Publisher(http, _ => "secret").CheckFolderAsync(Destination, false, default);
        Assert.Equal("https://tenant.sharepoint.com/view", folder.PublicUrl);
    }

    [Theory]
    [InlineData("../drive", "folder")]
    [InlineData("drive", "https://example.com")]
    public void RejectsDestinationPathInjection(string drive, string folder) =>
        Assert.Throws<ArgumentException>(() => (Destination with { DriveId = drive, FolderId = folder }).Validate());

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
