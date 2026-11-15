using System.Net;
using System.Text.Json;

namespace CommuteCast.Infrastructure;

public sealed record M365ListeningItem(string Id, string Name, DateTimeOffset CreatedUtc, double? DurationSeconds);

public interface IM365ListeningLibrary
{
    Task<IReadOnlyList<M365ListeningItem>> ListAsync(M365Destination destination, CancellationToken ct);
    Task<Uri?> PlaybackUriAsync(M365Destination destination, string itemId, CancellationToken ct);
}

public sealed partial class M365Publisher
{
    private const string ListeningFields = "id,name,file,size,createdDateTime,audio,remoteItem";
    public const string ListeningMediaSources = "https://*.sharepoint.com https://*.1drv.com https://*.storage.live.com https://*.sharepoint-df.com";

    public async Task<IReadOnlyList<M365ListeningItem>> ListAsync(M365Destination destination, CancellationToken ct)
    {
        var token = await TokenAsync(destination, ct);
        var children = new Uri(Item(destination) + "/children");
        string? next = children + "?$select=" + ListeningFields;
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var items = new Dictionary<string, M365ListeningItem>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!Uri.TryCreate(next, UriKind.Absolute, out var pageUri) ||
                pageUri.GetLeftPart(UriPartial.Path) != children.AbsoluteUri ||
                pageUri.UserInfo.Length != 0 || pageUri.Fragment.Length != 0 ||
                !pages.Add(pageUri.AbsoluteUri))
                throw new IOException("Microsoft 365 returned an invalid listening library page.");

            using var response = await GraphAsync(HttpMethod.Get, pageUri.AbsoluteUri, token, ct);
            CheckListening(response);
            using var page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            foreach (var item in page.RootElement.GetProperty("value").EnumerateArray())
            {
                if (ReadListeningItem(item) is { } mp3) items.TryAdd(mp3.Id, mp3);
            }
            next = PropertyString(page.RootElement, "@odata.nextLink");
        }
        return items.Values.OrderByDescending(item => item.CreatedUtc).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<Uri?> PlaybackUriAsync(M365Destination destination, string itemId, CancellationToken ct)
    {
        if (!M365Destination.SafeId(itemId)) return null;
        var token = await TokenAsync(destination, ct);
        var url = $"https://graph.microsoft.com/v1.0/drives/{Uri.EscapeDataString(destination.DriveId)}/items/{Uri.EscapeDataString(itemId)}";
        using var response = await GraphAsync(HttpMethod.Get, url + "?$select=" + ListeningFields + ",parentReference,@microsoft.graph.downloadUrl", token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        CheckListening(response);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var item = json.RootElement;
        if (ReadListeningItem(item)?.Id != itemId || !item.TryGetProperty("parentReference", out var parent) ||
            PropertyString(parent, "id") != destination.FolderId || PropertyString(parent, "driveId") != destination.DriveId)
            return null;

        if (!Uri.TryCreate(PropertyString(item, "@microsoft.graph.downloadUrl"), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !ListeningMediaSources.Split(' ').Any(source => uri.Host.EndsWith(source["https://*".Length..], StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Microsoft 365 returned an unsupported playback address.");
        return uri;
    }

    private static M365ListeningItem? ReadListeningItem(JsonElement item)
    {
        var id = PropertyString(item, "id");
        var name = PropertyString(item, "name");
        if (!M365Destination.SafeId(id) || name is null || !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
            !item.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object ||
            item.TryGetProperty("remoteItem", out var remote) && remote.ValueKind != JsonValueKind.Null ||
            !item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0)
            return null;

        var created = item.TryGetProperty("createdDateTime", out var date) && date.TryGetDateTimeOffset(out var utc) ? utc : default;
        double? duration = item.TryGetProperty("audio", out var audio) && audio.ValueKind == JsonValueKind.Object &&
            audio.TryGetProperty("duration", out var ms) && ms.TryGetInt64(out var milliseconds) && milliseconds > 0
            ? milliseconds / 1000d : null;
        return new(id!, name, created, duration);
    }

    private static string? PropertyString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void CheckListening(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Microsoft 365 could not open the listening folder (HTTP {(int)response.StatusCode}). Check the folder and its permissions in Studio settings.");
    }
}
