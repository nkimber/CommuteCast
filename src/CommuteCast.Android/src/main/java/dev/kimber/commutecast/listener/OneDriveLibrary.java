package dev.kimber.commutecast.listener;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.format.DateTimeFormatter;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;

final class OneDriveLibrary {
    static final String GRAPH = "https://graph.microsoft.com/v1.0";
    private static final String FILE_FIELDS = "id,name,size,file,remoteItem,createdDateTime,audio";
    private static final DateTimeFormatter EXPORT_TIMESTAMP = DateTimeFormatter.ofPattern("uuuuMMdd-HHmmss")
        .withResolverStyle(java.time.format.ResolverStyle.STRICT);
    private final JsonRequest request;

    record Folder(String drive, String item) {
        Folder {
            if (!LibrarySettings.validItemId(drive) || !LibrarySettings.validItemId(item))
                throw new IllegalArgumentException("Invalid OneDrive folder identity.");
        }
        String driveUrl() { return GRAPH + "/drives/" + segment(drive) + "/items/"; }
        String url() { return driveUrl() + segment(item); }
    }

    record Episode(String id, String title, long created, long durationMs) { }

    interface JsonRequest {
        JSONObject get(URI address, String token) throws IOException, JSONException;
    }

    OneDriveLibrary(JsonRequest request) { this.request = request; }

    Folder resolve(LibrarySettings settings, String token) throws IOException, JSONException {
        if (!settings.driveId().isEmpty()) return new Folder(settings.driveId(), settings.folderId());
        StringBuilder url = new StringBuilder(GRAPH + "/me/drive/root:");
        for (String part : settings.path().split("/")) url.append('/').append(segment(part));
        JSONObject item = request.get(URI.create(url + "?$select=id,folder,parentReference"), token);
        JSONObject parent = item.optJSONObject("parentReference");
        if (item.optJSONObject("folder") == null || parent == null)
            throw new IOException("Choose a folder under your OneDrive or enter its drive and folder IDs.");
        return new Folder(parent.optString("driveId"), item.optString("id"));
    }

    List<Episode> episodes(Folder folder, String token) throws IOException, JSONException {
        URI initial = URI.create(folder.url() + "/children?$select=" + FILE_FIELDS);
        URI current = initial;
        var visited = new HashSet<URI>();
        var episodes = new LinkedHashMap<String, Episode>();
        while (current != null) {
            if (!graphHost(current) || !initial.getPath().equals(current.getPath()) || !visited.add(current))
                throw new IOException("OneDrive returned an invalid library page.");
            JSONObject response = request.get(current, token);
            JSONArray entries = response.getJSONArray("value");
            for (int i = 0; i < entries.length(); i++) {
                Episode episode = episode(entries.getJSONObject(i));
                if (episode != null) episodes.putIfAbsent(episode.id(), episode);
            }
            String next = response.optString("@odata.nextLink");
            current = next.isEmpty() ? null : URI.create(next);
        }
        var sorted = new ArrayList<>(episodes.values());
        sorted.sort(Comparator.comparingLong(Episode::created).reversed().thenComparing(Episode::title));
        return sorted;
    }

    URI playback(Folder folder, String id, String token) throws IOException, JSONException {
        if (!LibrarySettings.validItemId(id)) throw new IOException("The episode ID is invalid.");
        JSONObject file = request.get(URI.create(folder.driveUrl() + segment(id) + "?$select=" +
            FILE_FIELDS + ",parentReference,@microsoft.graph.downloadUrl"), token);
        Episode episode = episode(file);
        JSONObject parent = file.optJSONObject("parentReference");
        if (episode == null || !episode.id().equals(id) || parent == null ||
            !folder.item().equals(parent.optString("id")) || !folder.drive().equals(parent.optString("driveId")))
            throw new IOException("The MP3 is no longer in the listening folder.");
        URI address = URI.create(file.optString("@microsoft.graph.downloadUrl"));
        if (!mediaHost(address)) throw new IOException("OneDrive returned an unsupported playback address.");
        return address;
    }

    private static Episode episode(JSONObject item) {
        String id = item.optString("id");
        String name = item.optString("name");
        if (!LibrarySettings.validItemId(id) || !name.toLowerCase(Locale.ROOT).endsWith(".mp3") ||
            item.optJSONObject("file") == null || !item.isNull("remoteItem") || item.optLong("size") <= 0)
            return null;
        JSONObject audio = item.optJSONObject("audio");
        long created = 0L;
        try { created = Instant.parse(item.optString("createdDateTime")).toEpochMilli(); }
        catch (DateTimeParseException ignored) { }
        return new Episode(id, title(name), created, audio == null ? 0 : Math.max(0, audio.optLong("duration")));
    }

    static String title(String filename) {
        String stem = filename.substring(0, filename.length() - 4);
        int end = stem.length() - 33;
        if (end > 16 && stem.charAt(15) == '-' && stem.charAt(end) == '-' &&
            stem.substring(end + 1).matches("[0-9a-fA-F]{32}")) {
            try {
                EXPORT_TIMESTAMP.parse(stem.substring(0, 15));
                return stem.substring(16, end);
            } catch (DateTimeParseException ignored) { }
        }
        return stem.isEmpty() ? filename : stem;
    }

    static JSONObject get(URI address, String token) throws IOException, JSONException {
        if (!graphHost(address)) throw new IOException("The OneDrive address is invalid.");
        HttpURLConnection connection = (HttpURLConnection) address.toURL().openConnection();
        connection.setInstanceFollowRedirects(false);
        connection.setConnectTimeout(15_000);
        connection.setReadTimeout(20_000);
        connection.setRequestProperty("Accept", "application/json");
        connection.setRequestProperty("Authorization", "Bearer " + token);
        try {
            int status = connection.getResponseCode();
            if (status != 200) throw new IOException(switch (status) {
                case 401 -> "Microsoft sign-in expired. Connect OneDrive again.";
                case 403 -> "OneDrive denied access. Check folder permissions and account consent.";
                case 404 -> "OneDrive could not find the listening folder or episode.";
                case 429 -> "OneDrive is busy. Try again in a moment.";
                default -> "OneDrive returned HTTP " + status + ".";
            });
            try (InputStream input = connection.getInputStream()) {
                byte[] chunk = new byte[8192];
                ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                for (int count; (count = input.read(chunk)) != -1;) {
                    if (bytes.size() + count > 8 * 1024 * 1024) throw new IOException("The OneDrive response was too large.");
                    bytes.write(chunk, 0, count);
                }
                return new JSONObject(new String(bytes.toByteArray(), StandardCharsets.UTF_8));
            }
        } finally {
            connection.disconnect();
        }
    }

    private static boolean graphHost(URI address) {
        return secure(address) && address.getHost().equalsIgnoreCase("graph.microsoft.com");
    }

    private static boolean mediaHost(URI address) {
        if (!secure(address)) return false;
        String host = address.getHost().toLowerCase(Locale.ROOT);
        return host.endsWith(".sharepoint.com") || host.endsWith(".1drv.com") ||
            host.endsWith(".storage.live.com") || host.endsWith(".sharepoint-df.com");
    }

    private static boolean secure(URI address) {
        return "https".equalsIgnoreCase(address.getScheme()) && address.getHost() != null &&
            (address.getPort() == -1 || address.getPort() == 443) &&
            address.getUserInfo() == null && address.getFragment() == null;
    }

    private static String segment(String value) {
        try { return URLEncoder.encode(value, "UTF-8").replace("+", "%20"); }
        catch (java.io.UnsupportedEncodingException impossible) { throw new AssertionError(impossible); }
    }
}
