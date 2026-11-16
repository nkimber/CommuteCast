package dev.kimber.commutecast.listener;

import android.app.Application;
import android.content.SharedPreferences;

import androidx.media3.common.MediaItem;
import androidx.media3.common.MediaMetadata;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DataSource;
import androidx.media3.datasource.DefaultHttpDataSource;

import java.io.IOException;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class ListeningApp extends Application {
    record Track(String key, String account, OneDriveLibrary.Folder folder, OneDriveLibrary.Episode episode) {
        MediaItem mediaItem() {
            return new MediaItem.Builder().setMediaId(key)
                .setUri("commutecast://onedrive/" + key).setMimeType("audio/mpeg")
                .setMediaMetadata(new MediaMetadata.Builder().setTitle(episode.title()).setAlbumTitle("CommuteCast").build())
                .build();
        }
    }

    record Bookmark(long positionMs, long durationMs, long playedAt, boolean finished) {
        static final long REWIND_MS = 20_000L;
        long resumeAt() { return finished ? 0 : Math.max(0L, positionMs - REWIND_MS); }
    }

    private final ExecutorService executor = Executors.newFixedThreadPool(2);
    private final OneDriveLibrary graph = new OneDriveLibrary(OneDriveLibrary::get);
    private SharedPreferences prefs;
    private MicrosoftSignIn signIn;
    private volatile LibrarySettings settings;
    private volatile List<Track> catalog = Collections.emptyList();
    private long revision;

    @Override public void onCreate() {
        super.onCreate();
        signIn = new MicrosoftSignIn(this);
        prefs = getSharedPreferences("listening", MODE_PRIVATE);
        if (prefs.contains("clientId")) {
            try {
                String path = prefs.getString("path", "");
                String driveId = prefs.getString("driveId", "");
                String folderId = prefs.getString("folderId", "");
                if (!prefs.getBoolean("sharedRootLayout", false))
                    path = LibrarySettings.migrateLegacyPath(path, driveId, folderId);
                settings = new LibrarySettings(prefs.getString("clientId", ""), prefs.getString("tenant", ""),
                    path, driveId, folderId, prefs.getBoolean("shared", false));
                if (!prefs.getBoolean("sharedRootLayout", false))
                    prefs.edit().putString("path", settings.path()).putBoolean("sharedRootLayout", true).apply();
            } catch (IllegalArgumentException ignored) { }
        }
    }

    LibrarySettings settings() { return settings; }
    MicrosoftSignIn signIn() { return signIn; }
    ExecutorService executor() { return executor; }
    synchronized long revision() { return revision; }
    List<Track> tracks() { return catalog; }

    synchronized void setSettings(LibrarySettings value) {
        if (value.equals(settings)) return;
        settings = value;
        clearCatalog();
        prefs.edit().putString("clientId", value.clientId()).putString("tenant", value.tenant())
            .putString("path", value.path()).putString("driveId", value.driveId()).putString("folderId", value.folderId())
            .putBoolean("shared", value.shared()).putBoolean("sharedRootLayout", true).apply();
    }

    synchronized void clearCatalog() {
        catalog = Collections.emptyList();
        revision++;
    }

    void refresh() throws Exception {
        final LibrarySettings configured;
        final long generation;
        synchronized (this) {
            configured = settings;
            generation = revision;
        }
        if (configured == null) throw new MicrosoftSignIn.SignInRequired();
        MicrosoftSignIn.Account account = signIn.account(configured);
        OneDriveLibrary.Folder folder = graph.resolve(configured, account.token());
        var found = new ArrayList<Track>();
        for (var episode : graph.episodes(folder, account.token())) {
            String key = hash(account.key() + "/" + folder.drive() + "/" + episode.id());
            found.add(new Track(key, account.key(), folder, episode));
        }
        setCatalog(generation, found);
    }

    synchronized void setCatalog(long generation, List<Track> found) throws IOException {
        if (generation != revision) throw new IOException("The OneDrive settings changed. Refresh the library.");
        catalog = Collections.unmodifiableList(found);
    }

    @UnstableApi
    DataSource.Factory audioSource() {
        return new DefaultHttpDataSource.Factory().setConnectTimeoutMs(15_000).setReadTimeoutMs(20_000);
    }

    URI playbackUri(String key) throws Exception {
        long generation = revision();
        Track track = track(key);
        LibrarySettings configured = settings;
        if (track == null || configured == null) throw new IOException("Refresh the OneDrive library before playing this episode.");
        MicrosoftSignIn.Account account = signIn.account(configured);
        if (!track.account().equals(account.key()) || generation != revision())
            throw new MicrosoftSignIn.SignInRequired();
        URI uri = graph.playback(track.folder(), track.episode().id(), account.token());
        if (generation != revision()) throw new IOException("The listening folder changed.");
        return uri;
    }

    Track track(String key) {
        for (Track track : catalog) if (track.key().equals(key)) return track;
        return null;
    }

    Track latest() {
        Track latest = null;
        long time = 0;
        for (Track track : catalog) {
            Bookmark bookmark = bookmark(track.key());
            if (bookmark != null && !bookmark.finished() && bookmark.playedAt() > time) {
                latest = track;
                time = bookmark.playedAt();
            }
        }
        return latest;
    }

    Bookmark bookmark(String key) {
        String saved = prefs.getString("episode." + key, "");
        if (saved.isEmpty()) return null;
        String[] parts = saved.split(",");
        if (parts.length != 4) return null;
        try {
            return new Bookmark(Long.parseLong(parts[0]), Long.parseLong(parts[1]), Long.parseLong(parts[2]), parts[3].equals("1"));
        } catch (NumberFormatException ignored) {
            return null;
        }
    }

    void store(String key, Bookmark bookmark) {
        prefs.edit().putString("episode." + key, bookmark.positionMs() + "," + bookmark.durationMs() + "," +
            bookmark.playedAt() + "," + (bookmark.finished() ? "1" : "0")).apply();
    }

    private static String hash(String value) {
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            return android.util.Base64.encodeToString(digest, android.util.Base64.URL_SAFE | android.util.Base64.NO_WRAP | android.util.Base64.NO_PADDING);
        } catch (NoSuchAlgorithmException impossible) {
            throw new AssertionError(impossible);
        }
    }
}
