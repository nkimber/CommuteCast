package dev.kimber.commutecast.listener;

import android.app.Application;
import android.content.Context;
import android.content.SharedPreferences;

import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DataSource;
import androidx.media3.datasource.DefaultDataSource;
import androidx.test.runner.AndroidJUnitRunner;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.net.URI;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

@UnstableApi
public final class PlaybackTestRunner extends AndroidJUnitRunner {
    @Override public Application newApplication(ClassLoader loader, String name, Context context)
            throws ClassNotFoundException, IllegalAccessException, InstantiationException {
        return super.newApplication(loader, AudioFixtureApp.class.getName(), context);
    }

    public static final class AudioFixtureApp extends ListeningApp {
        private File audio;

        @Override public SharedPreferences getSharedPreferences(String name, int mode) {
            return super.getSharedPreferences(name.equals("listening") ? "listener-test-fixture" : name, mode);
        }

        @Override public void onCreate() {
            super.onCreate();
            getSharedPreferences("listening", MODE_PRIVATE).edit().clear().apply();
            setSettings(new LibrarySettings("5cb48470-dbdb-480f-8362-e19669d4027a", "", "", "", "", false));
            try {
                audio = new File(getCacheDir(), "playback-test.wav");
                writeAudio(audio);
                refresh();
            } catch (IOException e) {
                throw new IllegalStateException("Couldn’t create the playback fixture.", e);
            }
        }

        @Override void refresh() throws IOException {
            var folder = new OneDriveLibrary.Folder("fixture-drive", "fixture-folder");
            setCatalog(revision(), Arrays.asList(
                new Track("one", "fixture", folder, new OneDriveLibrary.Episode("one", "Episode one", 2, 95_000)),
                new Track("two", "fixture", folder, new OneDriveLibrary.Episode("two", "Episode two", 1, 95_000))));
        }

        @Override URI playbackUri(String key) {
            if (track(key) == null) throw new IllegalArgumentException("Unexpected fixture key.");
            return audio.toURI();
        }

        @Override DataSource.Factory audioSource() {
            return new DefaultDataSource.Factory(this);
        }

        private static void writeAudio(File file) throws IOException {
            int sampleRate = 8000;
            int samples = 95 * sampleRate;
            ByteBuffer data = ByteBuffer.allocate(44 + samples * 2).order(ByteOrder.LITTLE_ENDIAN);
            data.put("RIFF".getBytes(StandardCharsets.US_ASCII)).putInt(36 + samples * 2);
            data.put("WAVEfmt ".getBytes(StandardCharsets.US_ASCII)).putInt(16);
            data.putShort((short) 1).putShort((short) 1).putInt(sampleRate).putInt(sampleRate * 2);
            data.putShort((short) 2).putShort((short) 16);
            data.put("data".getBytes(StandardCharsets.US_ASCII)).putInt(samples * 2);
            for (int i = 0; i < samples; i++)
                data.putShort((short) (1100 * Math.sin(2 * Math.PI * 220 * i / sampleRate)));
            try (var out = new FileOutputStream(file)) {
                out.write(data.array());
            }
        }
    }
}
