using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Web;

public static class ListeningApi
{
    public static void MapListeningApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/listen").RequireAuthorization();
        api.MapGet("/library", async (WebRuntime runtime, IM365ListeningLibrary library, CancellationToken ct) =>
        {
            var destination = runtime.WebSettings.Destination;
            if (!destination.Configured) return new ListeningCatalog(false, []);

            var published = runtime.ListeningTitles(destination);
            var mp3s = await library.ListAsync(destination, ct);
            return new ListeningCatalog(true, mp3s.Select(item =>
            {
                var known = published.GetValueOrDefault(item.Id);
                return new ListeningEpisode(ItemKey(destination, item.Id), item.Id,
                    known?.Title ?? ExportPublisher.DisplayTitle(item.Name), known?.Duration ?? item.DurationSeconds);
            }).ToArray());
        });
        api.MapGet("/media/{id}/{key}", async (string id, string key, WebRuntime runtime, IM365ListeningLibrary library, CancellationToken ct) =>
        {
            var destination = runtime.WebSettings.Destination;
            if (!destination.Configured || ItemKey(destination, id) != key) return Results.NotFound();
            var uri = await library.PlaybackUriAsync(destination, id, ct);
            return uri is null ? Results.NotFound() : Results.Redirect(uri.AbsoluteUri, preserveMethod: true);
        });
    }

    private static string ItemKey(M365Destination destination, string id) => Job.Hash(destination.DriveId + "/" + id);

    private sealed record ListeningCatalog(bool Configured, ListeningEpisode[] Episodes);
    private sealed record ListeningEpisode(string Key, string Id, string Title, double? DurationSeconds);
}
