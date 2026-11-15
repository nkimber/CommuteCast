using System.Net;
using System.Net.Http.Json;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public sealed class M365ListeningTests
{
    private const string Children = "https://graph.microsoft.com/v1.0/drives/drive/items/folder/children";
    private static readonly M365Destination Destination = new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "drive", "folder");

    [Fact]
    public async Task ListsPlayableFilesAcrossPagesWithoutReturningDownloadTokens()
    {
        using var http = Client(request =>
        {
            Assert.Equal(Children, request.RequestUri!.GetLeftPart(UriPartial.Path));
            return request.RequestUri.Query.Contains("skiptoken")
                ? Json(new { value = new[] { Item("new", "New.MP3"), Item("old", "Older.mp3") } })
                : Json(new Dictionary<string, object>
                {
                    ["value"] = new object[] { Item("old", "Older.mp3"), Item("txt", "notes.txt"),
                        new { id = "dir", name = "Folder.mp3", folder = new { }, size = 50 } },
                    ["@odata.nextLink"] = Children + "?$skiptoken=next"
                });
        });
        var items = await new M365Publisher(http, _ => "secret").ListAsync(Destination, default);
        Assert.Equal(new[] { "new", "old" }, items.Select(item => item.Id));
        Assert.All(items, item => Assert.Equal(120.5, item.DurationSeconds));
    }

    [Theory]
    [InlineData("https://outside.example/v1.0/drives/drive/items/folder/children")]
    [InlineData("https://graph.microsoft.com/v1.0/drives/drive/items/other/children")]
    public async Task RejectsPaginationOutsideTheConfiguredFolder(string next)
    {
        using var http = Client(request =>
        {
            Assert.Equal(Children, request.RequestUri!.GetLeftPart(UriPartial.Path));
            return Json(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next });
        });
        await Assert.ThrowsAsync<IOException>(() => new M365Publisher(http, _ => "secret").ListAsync(Destination, default));
    }

    [Fact]
    public async Task FollowsNextLinkWhenGraphUsesUnescapedDriveAndFolderIds()
    {
        const string path = "/v1.0/drives/drive!1/items/folder!2/children";
        using var http = Client(request =>
        {
            Assert.Equal(path, Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));
            return request.RequestUri.Query.Contains("skiptoken")
                ? Json(new { value = new[] { Item("old", "Older.mp3") } })
                : Json(new Dictionary<string, object>
                {
                    ["value"] = Array.Empty<object>(),
                    ["@odata.nextLink"] = "https://graph.microsoft.com" + path + "?$skiptoken=next"
                });
        });
        var items = await new M365Publisher(http, _ => "secret").ListAsync(
            Destination with { DriveId = "drive!1", FolderId = "folder!2" }, default);
        Assert.Equal("old", Assert.Single(items).Id);
    }

    [Theory]
    [InlineData("drive", "folder", "item", true)]
    [InlineData("other", "folder", "item", false)]
    [InlineData("drive", "other", "item", false)]
    [InlineData("drive", "folder", "different-id", false)]
    public async Task ResolvesOnlyAnMp3InTheCurrentFolder(string drive, string folder, string responseId, bool expected)
    {
        using var http = Client(request =>
        {
            Assert.EndsWith("/drives/drive/items/item", request.RequestUri!.AbsolutePath);
            return Json(Item(responseId, "episode.mp3", drive, folder));
        });
        var uri = await new M365Publisher(http, _ => "secret").PlaybackUriAsync(Destination, "item", default);
        Assert.Equal(expected ? "https://tenant.sharepoint.com/audio?token=private" : null, uri?.AbsoluteUri);
    }

    [Fact]
    public async Task RejectsUnexpectedPlaybackHostsWithoutLeakingDownloadTokens()
    {
        using var http = Client(_ => Json(Item("item", "episode.mp3", url: "https://tenant.sharepoint.com.evil.example/?secret=private")));
        var error = await Assert.ThrowsAsync<IOException>(() => new M365Publisher(http, _ => "secret").PlaybackUriAsync(Destination, "item", default));
        Assert.DoesNotContain("secret", error.Message);
    }

    private static object Item(string id, string name, string drive = "drive", string folder = "folder",
        string url = "https://tenant.sharepoint.com/audio?token=private") => new Dictionary<string, object>
    {
        ["id"] = id, ["name"] = name, ["size"] = 100, ["file"] = new { mimeType = "audio/mpeg" },
        ["createdDateTime"] = id == "new" ? "2026-11-15T10:00:00Z" : "2025-03-10T10:00:00Z",
        ["audio"] = new { duration = 120500 }, ["parentReference"] = new { id = folder, driveId = drive },
        ["@microsoft.graph.downloadUrl"] = url
    };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> graph) => new(new Handler(request =>
    {
        if (request.RequestUri!.Host == "login.microsoftonline.com") return Json(new { access_token = "test-token" });
        Assert.Equal("graph.microsoft.com", request.RequestUri.Host);
        Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
        return graph(request);
    }));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
