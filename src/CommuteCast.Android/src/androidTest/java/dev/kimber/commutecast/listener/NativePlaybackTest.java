package dev.kimber.commutecast.listener;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.ComponentName;
import android.content.Intent;
import android.os.ParcelFileDescriptor;
import android.os.PowerManager;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;

import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.session.MediaController;
import androidx.media3.session.SessionToken;
import androidx.test.platform.app.InstrumentationRegistry;
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry;
import androidx.test.runner.lifecycle.Stage;
import org.junit.Test;

import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.function.Predicate;

import static org.junit.Assert.*;

@UnstableApi
public final class NativePlaybackTest {
    private final Instrumentation instrumentation = InstrumentationRegistry.getInstrumentation();

    @Test public void playsWhileLockedAndResumesBothEpisodesFromTheirOwnBookmarks() throws Exception {
        ListeningApp app = (ListeningApp) instrumentation.getTargetContext().getApplicationContext();
        var power = app.getSystemService(PowerManager.class);
        var token = new SessionToken(app, new ComponentName(app, PlaybackService.class));
        var controller = main(() -> new MediaController.Builder(app, token).buildAsync()).get(10, TimeUnit.SECONDS);
        Activity start = launch();
        try {
            click(start, "Play Episode one");
            await("Episode one is playing", () -> main(controller::isPlaying));
            await("periodic bookmark", () -> app.bookmark("one") != null && app.bookmark("one").positionMs() >= 4_000);
            main(() -> { controller.seekTo(56_250); return null; });
            await("first seek completes", () -> main(() -> controller.isPlaying() &&
                controller.getCurrentPosition() >= 56_400));
            long beforeLock = main(controller::getCurrentPosition);
            shell("input keyevent KEYCODE_SLEEP");
            await("screen is locked", () -> !power.isInteractive());
            await("playback advances with the screen locked", () -> main(() -> controller.isPlaying() &&
                controller.getCurrentPosition() >= beforeLock + 500));
            shell("cmd media_session dispatch pause");
            await("system media key pauses", () -> !main(controller::getPlayWhenReady));
            long paused = app.bookmark("one").positionMs();
            assertTrue(paused >= 56_250);
            shell("cmd media_session dispatch play");
            await("system media key resumes", () -> main(controller::isPlaying));
            assertNear(paused - 20_000, main(controller::getCurrentPosition));

            shell("input keyevent KEYCODE_WAKEUP");
            shell("wm dismiss-keyguard");
            click(start, "Play Episode two");
            await("second episode is playing", () -> main(() -> controller.isPlaying() &&
                controller.getCurrentMediaItem().mediaId.equals("two")));
            main(() -> { controller.seekTo(44_500); return null; });
            await("second seek completes", () -> main(() -> controller.isPlaying() &&
                controller.getCurrentPosition() >= 44_650));
            click(start, "Pause Episode two");
            await("second episode is paused", () -> !main(controller::getPlayWhenReady));
            long second = app.bookmark("two").positionMs();
            main(() -> {
                controller.clearMediaItems();
                start.finish();
                return null;
            });
            await("player is clear", () -> main(controller::getMediaItemCount) == 0);

            Activity reopened = launch();
            await("second title comes back on reopen", () -> main(() -> find(reopened.getWindow().getDecorView(),
                v -> v instanceof TextView && ((TextView) v).getText().toString().equals("Episode two")) != null));
            click(reopened, "Resume");
            await("resume button starts last episode", () -> main(() -> controller.isPlaying() &&
                controller.getCurrentMediaItem().mediaId.equals("two")));
            assertNear(second - 20_000, main(controller::getCurrentPosition));
            assertNotNull(app.bookmark("one"));
            assertFalse(app.bookmark("one").finished());

            main(() -> { controller.seekTo(94_800); return null; });
            await("native playback reaches end", () -> main(controller::getPlaybackState) == Player.STATE_ENDED);
            await("finished bookmark", () -> app.bookmark("two").finished());
            assertNull(main(() -> find(reopened.getWindow().getDecorView(),
                v -> "Replay Episode two".contentEquals(v.getContentDescription() == null ? "" : v.getContentDescription()))));
            click(reopened, "Show finished");
            await("finished episode revealed", () -> main(() -> find(reopened.getWindow().getDecorView(),
                v -> "Replay Episode two".contentEquals(v.getContentDescription() == null ? "" : v.getContentDescription())) != null));
            main(() -> { reopened.finish(); return null; });
        } finally {
            shell("input keyevent KEYCODE_WAKEUP");
            main(() -> {
                controller.pause();
                controller.release();
                start.finish();
                return null;
            });
        }
    }

    private Activity launch() {
        return instrumentation.startActivitySync(new Intent(instrumentation.getTargetContext(), ListenerActivity.class)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
    }

    private void click(Activity activity, String name) throws Exception {
        await("available action " + name, () -> main(() -> {
            if (ActivityLifecycleMonitorRegistry.getInstance().getLifecycleStageOf(activity) != Stage.RESUMED)
                return false;
            View view = find(activity.getWindow().getDecorView(), v -> v.isClickable() && v.isEnabled() &&
                (name.contentEquals(v.getContentDescription() == null ? "" : v.getContentDescription()) ||
                    v instanceof TextView && name.contentEquals(((TextView) v).getText())));
            if (view == null) return false;
            view.performClick();
            return true;
        }));
    }

    private static View find(View view, Predicate<View> match) {
        if (view.getVisibility() != View.VISIBLE) return null;
        if (match.test(view)) return view;
        if (view instanceof ViewGroup group) {
            for (int i = 0; i < group.getChildCount(); i++) {
                View child = find(group.getChildAt(i), match);
                if (child != null) return child;
            }
        }
        return null;
    }

    private void shell(String command) throws Exception {
        try (var out = new ParcelFileDescriptor.AutoCloseInputStream(instrumentation.getUiAutomation().executeShellCommand(command))) {
            byte[] bytes = new byte[512];
            while (out.read(bytes) >= 0) { }
        }
    }

    private <T> T main(Callable<T> action) throws Exception {
        var task = new FutureTask<>(action);
        instrumentation.runOnMainSync(task);
        return task.get();
    }

    private static void await(String description, Callable<Boolean> condition) throws Exception {
        long deadline = SystemClock.elapsedRealtime() + 12_000;
        while (SystemClock.elapsedRealtime() < deadline) {
            if (condition.call()) return;
            SystemClock.sleep(100);
        }
        fail(description);
    }

    private static void assertNear(long expected, long found) {
        assertTrue("Expected " + expected + " ms, got " + found, Math.abs(found - expected) < 1_500);
    }
}
