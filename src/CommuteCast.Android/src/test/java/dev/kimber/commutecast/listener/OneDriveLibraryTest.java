package dev.kimber.commutecast.listener;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import org.junit.Test;

import java.io.IOException;
import java.net.URI;
import java.util.List;

import static org.junit.Assert.*;

public class OneDriveLibraryTest {
    private static final String CLIENT = "5cb48470-dbdb-480f-8362-e19669d4027a";
    private static final OneDriveLibrary.Folder FOLDER = new OneDriveLibrary.Folder("b!drive", "folder");
    private static final String CONTENT = "https://contoso.sharepoint.com/audio?temporary=fixture";

    @Test public void resolvesNestedFolderWithEscapedPath() throws Exception {
        var library = new OneDriveLibrary((url, token) -> {
            assertEquals("/v1.0/me/drive/root:/Shared%20Folder/Ready%20to%20Listen", url.getRawPath());
            assertEquals("token", token);
            return new JSONObject().put("id", "folder").put("folder", new JSONObject())
                .put("parentReference", new JSONObject().put("driveId", "b!drive"));
        });
        var settings = new LibrarySettings(CLIENT, "", "Shared Folder/Ready to Listen", "", "", false);
        assertEquals(FOLDER, library.resolve(settings, "token"));
        assertEquals("https://graph.microsoft.com/Files.Read", settings.scope());
    }

    @Test public void rejectsIncompleteAndEscapingFolderSettings() {
        assertThrows(IllegalArgumentException.class, () -> new LibrarySettings(CLIENT, "", "Ready/../Other", "", "", false));
        assertThrows(IllegalArgumentException.class, () -> new LibrarySettings(CLIENT, "", "", "drive", "", false));
        assertThrows(IllegalArgumentException.class, () -> new LibrarySettings(CLIENT, "", "", "drive", "..", false));
    }

    @Test public void readsExistingMp3sAcrossGraphPagesAndDeduplicates() throws Exception {
        var first = file("old", "20261101-070000-Episode One-aedd81a92ad439ea9b628b1f130e1119.mp3");
        var second = file("new", "Episode Two.MP3").put("createdDateTime", "2026-11-15T20:00:00Z")
            .put("audio", new JSONObject().put("duration", 152000));
        var remote = file("shortcut", "Shortcut.mp3").put("remoteItem", new JSONObject());
        var library = new OneDriveLibrary((url, token) -> {
            assertEquals("/v1.0/drives/b!drive/items/folder/children", url.getPath());
            if (url.getQuery().contains("skiptoken")) return new JSONObject().put("value", new JSONArray().put(first).put(second));
            return new JSONObject().put("value", new JSONArray().put(first).put(remote).put(file("partial", "part.tmp")))
                .put("@odata.nextLink", OneDriveLibrary.GRAPH + "/drives/b!drive/items/folder/children?$skiptoken=2");
        });
        var entries = library.episodes(FOLDER, "token");
        assertEquals(List.of("Episode Two", "Episode One"), entries.stream().map(OneDriveLibrary.Episode::title).toList());
        assertEquals(152000, entries.get(0).durationMs());
    }

    @Test public void rejectsContinuationOutsideConfiguredFolder() {
        var library = new OneDriveLibrary((url, token) -> {
            assertEquals("graph.microsoft.com", url.getHost());
            assertEquals("/v1.0/drives/b!drive/items/folder/children", url.getPath());
            return new JSONObject().put("value", new JSONArray())
                .put("@odata.nextLink", "https://outside.example/v1.0/drives/b!drive/items/folder/children");
        });
        assertThrows(IOException.class, () -> library.episodes(FOLDER, "token"));
    }

    @Test public void acceptsShortLivedMediaOnlyForRequestedMp3InFolder() throws Exception {
        var library = new OneDriveLibrary((url, token) -> {
            assertEquals("/v1.0/drives/b!drive/items/one", url.getPath());
            return file("one", "episode.mp3").put("@microsoft.graph.downloadUrl", CONTENT);
        });
        assertEquals(URI.create(CONTENT), library.playback(FOLDER, "one", "token"));
        assertThrows(IOException.class,
            () -> library.playback(new OneDriveLibrary.Folder("b!drive", "other"), "one", "token"));
        var unsupported = new OneDriveLibrary((url, token) -> file("one", "episode.mp3")
            .put("@microsoft.graph.downloadUrl", "https://contoso.sharepoint.com.outside.example/audio"));
        assertThrows(IOException.class, () -> unsupported.playback(FOLDER, "one", "token"));
    }

    @Test public void resumesTwentySecondsEarlyAndClampsAtBeginning() {
        assertEquals(36_250, new ListeningApp.Bookmark(56_250, 120_000, 123L, false).resumeAt());
        assertEquals(0, new ListeningApp.Bookmark(15_000, 120_000, 123L, false).resumeAt());
        assertEquals(0, new ListeningApp.Bookmark(120_000, 120_000, 123L, true).resumeAt());
    }

    private static JSONObject file(String id, String name) throws JSONException {
        return new JSONObject().put("id", id).put("name", name).put("file", new JSONObject())
            .put("size", 1324).put("parentReference", new JSONObject().put("id", "folder").put("driveId", "b!drive"));
    }
}
