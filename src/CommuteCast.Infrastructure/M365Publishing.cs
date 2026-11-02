using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public sealed record M365Destination(string TenantId = "", string ClientId = "", string DriveId = "", string FolderId = "")
{
    public bool Configured => new[] { TenantId, ClientId, DriveId, FolderId }.All(s => !string.IsNullOrWhiteSpace(s));
    public void Validate()
    {
        if (!Guid.TryParse(TenantId, out _) || !Guid.TryParse(ClientId, out _) ||
            !SafeId(DriveId) || !SafeId(FolderId))
            throw new ArgumentException("Enter the tenant ID, application ID, drive ID and folder ID for your Microsoft 365 destination.");
    }
    private static bool SafeId(string? id) => id is { Length: > 0 and <= 250 } && id is not ("." or "..") && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!' or '.');
}

public sealed record M365Folder(string Name, string PublicUrl);
public sealed record M365Upload(string ItemId, string Name, string ETag, long Size);

public interface IM365Publisher
{
    Task<M365Folder> CheckFolderAsync(M365Destination destination, bool createPublicLink, CancellationToken ct);
    Task<M365Upload> UploadAsync(M365Destination destination, string path, string name, string expectedHash, CancellationToken ct);
}

/// <summary>Server-side Graph transport. Upload sessions never receive the Graph bearer token.</summary>
public sealed class M365Publisher(HttpClient http, Func<M365Destination, string?> getSecret) : IM365Publisher
{
    private static string Item(M365Destination d) => $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(d.DriveId)}/items/{Uri.EscapeDataString(d.FolderId)}";

    private async Task<string> TokenAsync(M365Destination destination, CancellationToken ct)
    {
        destination.Validate();
        var secret = getSecret(destination);
        if (string.IsNullOrWhiteSpace(secret)) throw new IOException("Save the Microsoft 365 application secret in Settings before publishing.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{destination.TenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = destination.ClientId, ["client_secret"] = secret,
                ["grant_type"] = "client_credentials", ["scope"] = "https://graph.microsoft.com/.default"
            })
        };
        using var response = await http.SendAsync(request, ct);
        await CheckAsync(response, "Microsoft 365 sign-in", ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("access_token").GetString() ?? throw new IOException("Microsoft 365 did not return an access token.");
    }

    private async Task<HttpResponseMessage> GraphAsync(HttpMethod method, string url, string token, CancellationToken ct, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<M365Folder> CheckFolderAsync(M365Destination destination, bool createPublicLink, CancellationToken ct)
    {
        var token = await TokenAsync(destination, ct);
        using var response = await GraphAsync(HttpMethod.Get, Item(destination), token, ct);
        await CheckAsync(response, "Destination folder check", ct);
        using var folder = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!folder.RootElement.TryGetProperty("folder", out _)) throw new IOException("The Microsoft 365 destination must be a folder.");
        using var permissions = await GraphAsync(HttpMethod.Get, Item(destination) + "/permissions", token, ct);
        await CheckAsync(permissions, "Folder sharing check", ct);
        using var sharing = await JsonDocument.ParseAsync(await permissions.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var url = sharing.RootElement.GetProperty("value").EnumerateArray().Select(PublicLink).FirstOrDefault(s => s.Length > 0) ?? "";
        if (url.Length == 0 && createPublicLink)
        {
            using var created = await GraphAsync(HttpMethod.Post, Item(destination) + "/createLink", token, ct,
                JsonContent.Create(new { type = "view", scope = "anonymous" }));
            await CheckAsync(created, "Read-only Anyone link creation", ct);
            using var createdJson = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            url = PublicLink(createdJson.RootElement);
        }
        if (url.Length == 0) throw new IOException("The folder needs a read-only Anyone link. Enable sharing in Settings, or check your tenant's sharing policy.");
        return new(folder.RootElement.GetProperty("name").GetString() ?? "CommuteCast", url);
    }

    private static string PublicLink(JsonElement permission)
    {
        if (permission.TryGetProperty("expirationDateTime", out var expiry) && expiry.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(expiry.GetString(), out var until) && until <= DateTimeOffset.UtcNow) return "";
        if (!permission.TryGetProperty("link", out var link) ||
            link.GetProperty("scope").GetString() != "anonymous" || link.GetProperty("type").GetString() != "view") return "";
        var raw = link.GetProperty("webUrl").GetString();
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri.AbsoluteUri : "";
    }

    public async Task<M365Upload> UploadAsync(M365Destination destination, string path, string name, string expectedHash, CancellationToken ct)
    {
        if (name != Path.GetFileName(name) || !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Publishing requires a safe MP3 filename.");
        Workspace.RejectReparsePoints(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Workspace.RejectFileReparsePoint(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (input.Length == 0 || Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != expectedHash)
            throw new IOException("The finished MP3 no longer matches its validated hash. The remote folder was not changed.");
        input.Position = 0;
        var token = await TokenAsync(destination, ct);
        var target = Item(destination) + ":/" + Uri.EscapeDataString(name);
        // A deterministic unique name makes a completed upload recoverable after a lost response.
        using (var existing = await GraphAsync(HttpMethod.Get, target, token, ct))
        {
            if (existing.IsSuccessStatusCode)
            {
                using var data = await JsonDocument.ParseAsync(await existing.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                await VerifyContentAsync(target + ":/content", token, expectedHash, ct);
                return Receipt(data.RootElement, input.Length, name);
            }
            if (existing.StatusCode != HttpStatusCode.NotFound) await CheckAsync(existing, "Existing publication check", ct);
        }
        using var sessionResponse = await GraphAsync(HttpMethod.Post, target + ":/createUploadSession", token, ct,
            JsonContent.Create(new { item = new Dictionary<string, string> { ["@microsoft.graph.conflictBehavior"] = "fail", ["name"] = name } }));
        await CheckAsync(sessionResponse, "Upload session creation", ct);
        using var session = await JsonDocument.ParseAsync(await sessionResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString();
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri) || uploadUri.Scheme != "https" ||
            !new[] { ".sharepoint.com", ".up.1drv.com", ".storage.live.com", ".sharepoint-df.com" }.Any(s => uploadUri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Microsoft 365 returned an unsupported upload host.");
        var buffer = new byte[10 * 320 * 1024]; // Graph requires non-final fragments to be multiples of 320 KiB.
        long offset = 0;
        while (offset < input.Length)
        {
            var count = await input.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, input.Length - offset)),
                (int)Math.Min(buffer.Length, input.Length - offset), cancellationToken: ct);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri) { Content = new ByteArrayContent(buffer, 0, count) };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, input.Length);
            using var uploaded = await http.SendAsync(request, ct);
            await CheckAsync(uploaded, "MP3 upload", ct);
            offset += count;
            if (uploaded.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                if (offset != input.Length) throw new IOException("Microsoft 365 finalized an incomplete upload.");
                using var result = await JsonDocument.ParseAsync(await uploaded.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var receipt = Receipt(result.RootElement, input.Length, name);
                await VerifyContentAsync(target + ":/content", token, expectedHash, ct);
                return receipt;
            }
            if (uploaded.StatusCode != HttpStatusCode.Accepted) throw new IOException("Microsoft 365 returned an unexpected upload state.");
        }
        throw new IOException("Microsoft 365 did not confirm the finished upload. Retry publishing to reconcile the saved MP3.");
    }

    private async Task VerifyContentAsync(string url, string token, string expectedHash, CancellationToken ct)
    {
        using var response = await GraphAsync(HttpMethod.Get, url, token, ct);
        await CheckAsync(response, "Published MP3 verification", ct);
        await using var remote = await response.Content.ReadAsStreamAsync(ct);
        if (Convert.ToHexString(await SHA256.HashDataAsync(remote, ct)) != expectedHash)
            throw new IOException("The file in Microsoft 365 differs from the validated MP3. It was preserved; resolve the conflict before retrying.");
    }

    private static M365Upload Receipt(JsonElement item, long size, string name)
    {
        if (item.GetProperty("size").GetInt64() != size || item.GetProperty("name").GetString() != name || !item.TryGetProperty("file", out _))
            throw new IOException("Microsoft 365 returned a different file identity or size.");
        return new(item.GetProperty("id").GetString() ?? throw new IOException("Missing uploaded file identity."), name,
            item.TryGetProperty("eTag", out var tag) ? tag.GetString() ?? "" : "", size);
    }

    private static async Task CheckAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        // Do not surface provider bodies, preauthenticated URLs, tokens or tenant details in errors.
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        throw new IOException($"{operation} failed (HTTP {(int)response.StatusCode}). " +
            (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? "Check application credentials, selected folder permissions and tenant sharing policy."
                : "The local MP3 is retained. Retry publishing after Microsoft 365 is available."));
    }
}
