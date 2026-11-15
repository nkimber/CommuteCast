package dev.kimber.commutecast.listener;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ComponentName;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.TextView;

import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.session.MediaController;
import androidx.media3.session.SessionToken;

import com.google.common.util.concurrent.ListenableFuture;
import com.microsoft.identity.client.AuthenticationCallback;
import com.microsoft.identity.client.IAuthenticationResult;
import com.microsoft.identity.client.exception.MsalException;

import java.util.Comparator;
import java.util.Locale;

@UnstableApi
public final class ListenerActivity extends Activity {
    private static final int PAPER = 0xfff3f3e9;
    private static final int GREEN = 0xff153c34;
    private static final int LIME = 0xffd4ed8b;
    private static final int INK = 0xff1d302a;
    private static final int MUTED = 0xff54645b;
    private static final int FAINT = 0xffdeebe4;
    private static final int LINE = 0xffd8ded2;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable updatePosition = new Runnable() {
        @Override public void run() {
            renderPlayer();
            handler.postDelayed(this, 1000);
        }
    };

    private ListeningApp app;
    private MediaController player;
    private ListenableFuture<MediaController> connecting;
    private LinearLayout hero;
    private LinearLayout playerControls;
    private LinearLayout rows;
    private TextView title;
    private TextView position;
    private TextView remaining;
    private TextView status;
    private TextView playerStatus;
    private Button primary;
    private Button rewind;
    private Button refresh;
    private Button connect;
    private CheckBox showFinished;
    private SeekBar slider;
    private boolean scrubbing;
    private boolean busy;
    private boolean needsSignIn;
    private long lastRefresh;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        app = (ListeningApp) getApplication();
        buildScreen();
        render();
    }

    @Override protected void onStart() {
        super.onStart();
        var token = new SessionToken(this, new ComponentName(this, PlaybackService.class));
        ListenableFuture<MediaController> future = new MediaController.Builder(this, token).buildAsync();
        connecting = future;
        future.addListener(() -> {
            if (connecting != future || isDestroyed()) return;
            try {
                player = future.get();
                player.addListener(new Player.Listener() {
                    @Override public void onEvents(Player player, Player.Events events) {
                        if (events.containsAny(Player.EVENT_PLAY_WHEN_READY_CHANGED,
                                Player.EVENT_IS_PLAYING_CHANGED, Player.EVENT_MEDIA_ITEM_TRANSITION,
                                Player.EVENT_PLAYBACK_STATE_CHANGED, Player.EVENT_PLAYER_ERROR)) render();
                    }
                });
                render();
            } catch (Exception e) {
                playerStatus.setText("Couldn’t open the audio player. Reopen CommuteCast to try again.");
            }
        }, this::runOnUiThread);
        handler.post(updatePosition);
    }

    @Override protected void onResume() {
        super.onResume();
        if (app.settings() != null && System.currentTimeMillis() - lastRefresh > 20_000) refresh();
    }

    @Override protected void onStop() {
        handler.removeCallbacks(updatePosition);
        if (connecting != null) MediaController.releaseFuture(connecting);
        connecting = null;
        player = null;
        super.onStop();
    }

    private void buildScreen() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(PAPER);
        LinearLayout body = column();
        body.setPadding(dp(18), dp(12), dp(18), dp(24));
        scroll.addView(body);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> {
            if (android.os.Build.VERSION.SDK_INT >= 30) {
                var bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                scroll.setPadding(bars.left, bars.top, bars.right, bars.bottom);
                return WindowInsets.CONSUMED;
            }
            return insets;
        });
        setContentView(scroll);

        LinearLayout header = row();
        ImageView icon = new ImageView(this);
        icon.setImageResource(R.drawable.ic_commutecast);
        header.addView(icon, new LinearLayout.LayoutParams(dp(38), dp(38)));
        TextView brand = label("CommuteCast", 19, GREEN, true);
        var brandLayout = weight();
        brandLayout.leftMargin = dp(8);
        header.addView(brand, brandLayout);
        Button settings = button("Settings", GREEN, PAPER);
        settings.setOnClickListener(v -> configuration());
        header.addView(settings);
        body.addView(header);
        space(body, 14);

        hero = column();
        hero.setBackground(card(GREEN, GREEN, 20));
        hero.setPadding(dp(18), dp(16), dp(18), dp(16));
        hero.addView(label("Continue listening", 16, LIME, true));
        title = label("", 25, Color.WHITE, true);
        space(hero, 10);
        hero.addView(title);
        playerControls = column();
        slider = new SeekBar(this);
        slider.setMax(1000);
        slider.setContentDescription("Playback position");
        slider.setProgressTintList(ColorStateList.valueOf(LIME));
        slider.setThumbTintList(ColorStateList.valueOf(LIME));
        slider.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onStartTrackingTouch(SeekBar bar) { scrubbing = true; }
            @Override public void onStopTrackingTouch(SeekBar bar) {
                scrubbing = false;
                seek(bar.getProgress());
            }
            @Override public void onProgressChanged(SeekBar bar, int value, boolean fromUser) {
                if (fromUser && !scrubbing) seek(value);
            }
        });
        playerControls.addView(slider);
        LinearLayout timing = row();
        position = label("", 14, FAINT, false);
        remaining = label("", 14, FAINT, false);
        timing.addView(position, weight());
        timing.addView(remaining);
        playerControls.addView(timing);
        space(playerControls, 10);
        LinearLayout actions = row();
        primary = button("Resume", INK, LIME);
        primary.setMinHeight(dp(54));
        primary.setOnClickListener(v -> {
            var track = selected();
            if (track != null) toggle(track);
        });
        actions.addView(primary, weight());
        rewind = button("↶ 20 sec", Color.WHITE, 0xff2d5349);
        rewind.setMinHeight(dp(54));
        rewind.setContentDescription("Back 20 seconds");
        rewind.setOnClickListener(v -> { if (player != null) player.seekBack(); });
        var rewindLayout = new LinearLayout.LayoutParams(-2, -2);
        rewindLayout.leftMargin = dp(8);
        actions.addView(rewind, rewindLayout);
        playerControls.addView(actions);
        hero.addView(playerControls);
        playerStatus = label("", 14, 0xfff7e5ae, false);
        hero.addView(playerStatus);
        body.addView(hero);
        space(body, 19);

        LinearLayout heading = row();
        heading.addView(label("Library", 25, INK, true), weight());
        refresh = button("Refresh", GREEN, PAPER);
        refresh.setOnClickListener(v -> refresh());
        heading.addView(refresh);
        body.addView(heading);
        showFinished = new CheckBox(this);
        showFinished.setText("Show finished");
        showFinished.setTextColor(MUTED);
        showFinished.setMinHeight(dp(48));
        showFinished.setOnCheckedChangeListener((v, checked) -> renderLibrary());
        body.addView(showFinished);
        status = label("", 15, MUTED, false);
        body.addView(status);
        connect = button("Connect OneDrive", GREEN, LIME);
        connect.setOnClickListener(v -> {
            if (app.settings() == null) configuration();
            else interactiveSignIn();
        });
        body.addView(connect);
        rows = column();
        body.addView(rows);
    }

    private void configuration() {
        if (busy) return;
        LibrarySettings old = app.settings();
        LinearLayout fields = column();
        fields.setPadding(dp(20), dp(4), dp(20), dp(10));
        fields.addView(label("Register this installation as an Android app in Microsoft Entra.", 14, MUTED, false));
        try {
            TextView identity = label(getPackageName() + "\nSignature hash: " + app.signIn().signatureHash(), 14, GREEN, false);
            identity.setTextIsSelectable(true);
            fields.addView(identity);
        } catch (Exception ignored) { }
        EditText client = field(fields, "Android application (client) ID", old == null ? "" : old.clientId());
        EditText tenant = field(fields, "Directory (tenant) ID · optional", old == null || old.tenant().equals("common") ? "" : old.tenant());
        EditText path = field(fields, "Folder path in your OneDrive", old == null ? LibrarySettings.DEFAULT_PATH : old.path());
        fields.addView(label("For a folder identified by drive and item IDs, enter both IDs below.", 14, MUTED, false));
        EditText drive = field(fields, "Drive ID · optional", old == null ? "" : old.driveId());
        EditText folder = field(fields, "Folder item ID · optional", old == null ? "" : old.folderId());
        CheckBox shared = new CheckBox(this);
        shared.setText("Read files shared with this account (Files.Read.All)");
        shared.setChecked(old != null && old.shared());
        fields.addView(shared);
        TextView issue = label("", 14, 0xff922d29, false);
        fields.addView(issue);
        ScrollView scroller = new ScrollView(this);
        scroller.addView(fields);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("OneDrive settings").setView(scroller)
            .setPositiveButton("Save", null).setNegativeButton("Cancel", null)
            .setNeutralButton(old == null ? null : "Sign out", (d, w) -> signOut(old)).create();
        dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            try {
                var configured = new LibrarySettings(value(client), value(tenant), value(path),
                    value(drive), value(folder), shared.isChecked());
                if (!configured.equals(old)) {
                    stopPlayback();
                    app.setSettings(configured);
                }
                dialog.dismiss();
                refresh();
            } catch (IllegalArgumentException e) {
                issue.setText(e.getMessage());
            }
        }));
        dialog.show();
    }

    private void interactiveSignIn() {
        if (busy) return;
        LibrarySettings settings = app.settings();
        busy = true;
        status.setText("Opening Microsoft sign-in…");
        updateActions();
        app.executor().execute(() -> {
            try {
                var signIn = app.signIn().interactive(settings);
                onScreen(() -> {
                    if (!settings.equals(app.settings())) {
                        endWork("OneDrive settings changed. Connect again.");
                        return;
                    }
                    try {
                        signIn.start(this, new AuthenticationCallback() {
                            @Override public void onSuccess(IAuthenticationResult result) {
                                onScreen(() -> {
                                    app.clearCatalog();
                                    busy = false;
                                    refresh();
                                });
                            }
                            @Override public void onError(MsalException error) {
                                onScreen(() -> endWork(MicrosoftSignIn.issue(error)));
                            }
                            @Override public void onCancel() { onScreen(() -> endWork("Microsoft sign-in cancelled.")); }
                        });
                    } catch (RuntimeException e) {
                        endWork(MicrosoftSignIn.issue(e));
                    }
                });
            } catch (Exception e) {
                onScreen(() -> endWork(MicrosoftSignIn.issue(e)));
            }
        });
    }

    private void signOut(LibrarySettings settings) {
        if (busy) return;
        stopPlayback();
        app.clearCatalog();
        busy = true;
        status.setText("Signing out…");
        render();
        app.executor().execute(() -> {
            try {
                app.signIn().signOut(settings);
                onScreen(() -> {
                    needsSignIn = true;
                    endWork("Connect your Microsoft account to load the listening folder.");
                });
            } catch (Exception e) {
                onScreen(() -> endWork(MicrosoftSignIn.issue(e)));
            }
        });
    }

    private void refresh() {
        if (busy || app.settings() == null) return;
        busy = true;
        status.setText("Loading your OneDrive library…");
        updateActions();
        long revision = app.revision();
        app.executor().execute(() -> {
            try {
                app.refresh();
                onScreen(() -> {
                    if (revision != app.revision()) {
                        busy = false;
                        refresh();
                        return;
                    }
                    needsSignIn = false;
                    lastRefresh = System.currentTimeMillis();
                    if (currentKey() != null && app.track(currentKey()) == null) stopPlayback();
                    endWork("");
                });
            } catch (Exception e) {
                onScreen(() -> {
                    if (revision != app.revision()) {
                        busy = false;
                        refresh();
                        return;
                    }
                    needsSignIn = e instanceof MicrosoftSignIn.SignInRequired || e instanceof MsalException;
                    if (e instanceof MicrosoftSignIn.SignInRequired) {
                        stopPlayback();
                        app.clearCatalog();
                    }
                    endWork(MicrosoftSignIn.issue(e));
                });
            }
        });
    }

    private void endWork(String message) {
        busy = false;
        status.setText(message);
        render();
    }

    private void onScreen(Runnable action) {
        runOnUiThread(() -> { if (!isDestroyed() && !isFinishing()) action.run(); });
    }

    private void stopPlayback() {
        if (player != null) {
            player.pause();
            player.clearMediaItems();
        }
    }

    private String currentKey() {
        return player == null || player.getCurrentMediaItem() == null ? null : player.getCurrentMediaItem().mediaId;
    }

    private boolean sameTrack(ListeningApp.Track track) { return track.key().equals(currentKey()); }

    private ListeningApp.Track selected() {
        var playing = app.track(currentKey());
        if (playing != null && player != null && player.getPlaybackState() != Player.STATE_ENDED) return playing;
        return app.latest();
    }

    private void toggle(ListeningApp.Track track) {
        if (player == null) return;
        if (sameTrack(track) && player.getPlayWhenReady() && player.getPlayerError() == null &&
                player.getPlaybackState() != Player.STATE_ENDED) {
            player.pause();
        } else if (sameTrack(track)) {
            player.play();
        } else {
            var bookmark = app.bookmark(track.key());
            player.setMediaItem(track.mediaItem(), bookmark == null ? 0 : bookmark.resumeAt());
            player.prepare();
            player.play();
        }
    }

    private void seek(int progress) {
        var track = selected();
        if (track != null && sameTrack(track) && player.getDuration() > 0)
            player.seekTo(player.getDuration() * progress / 1000);
    }

    private String action(ListeningApp.Track track) {
        if (player != null && sameTrack(track)) {
            if (player.getPlayerError() != null) return "Retry";
            if (player.getPlaybackState() == Player.STATE_ENDED) return "Replay";
            if (player.getPlayWhenReady()) return "Pause";
        }
        var bookmark = app.bookmark(track.key());
        return bookmark == null ? "Play" : bookmark.finished() ? "Replay" : "Resume";
    }

    private void render() {
        renderPlayer();
        renderLibrary();
        updateActions();
    }

    private void renderPlayer() {
        var track = selected();
        playerControls.setVisibility(track == null ? View.GONE : View.VISIBLE);
        if (track == null) {
            title.setText("Choose an episode from your library");
            playerStatus.setText("");
            playerStatus.setVisibility(View.GONE);
            return;
        }
        title.setText(track.episode().title());
        primary.setText(action(track));
        primary.setEnabled(player != null);
        var bookmark = app.bookmark(track.key());
        boolean loaded = player != null && sameTrack(track) && player.getDuration() > 0;
        long duration = loaded ? player.getDuration() : bookmark == null ? track.episode().durationMs() : bookmark.durationMs();
        long elapsed = loaded ? player.getCurrentPosition() : bookmark == null ? 0 : bookmark.positionMs();
        slider.setEnabled(loaded && player.isCurrentMediaItemSeekable());
        if (!scrubbing) slider.setProgress(duration > 0 ? (int) Math.min(1000, 1000 * elapsed / duration) : 0);
        rewind.setEnabled(slider.isEnabled() && elapsed > 0);
        position.setText(time(elapsed));
        remaining.setText(duration > 0 ? time(Math.max(0, duration - elapsed)) + " left" : "");
        boolean failed = player != null && sameTrack(track) && player.getPlayerError() != null;
        boolean buffering = player != null && sameTrack(track) && player.getPlayWhenReady() &&
            player.getPlaybackState() == Player.STATE_BUFFERING;
        String notice = failed ? "Couldn’t play this MP3. Tap Retry or refresh OneDrive." : buffering ? "Opening episode…" : "";
        playerStatus.setText(notice);
        playerStatus.setVisibility(notice.isEmpty() ? View.GONE : View.VISIBLE);
    }

    private void renderLibrary() {
        rows.removeAllViews();
        var tracks = app.tracks();
        var inProgress = new java.util.ArrayList<ListeningApp.Track>();
        var unplayed = new java.util.ArrayList<ListeningApp.Track>();
        var finished = new java.util.ArrayList<ListeningApp.Track>();
        for (var track : tracks) {
            var bookmark = app.bookmark(track.key());
            if (bookmark == null) unplayed.add(track);
            else if (bookmark.finished()) finished.add(track);
            else inProgress.add(track);
        }
        inProgress.sort(Comparator.comparingLong((ListeningApp.Track t) -> app.bookmark(t.key()).playedAt()).reversed());
        group("In progress", inProgress);
        group("Unplayed", unplayed);
        if (showFinished.isChecked()) group("Finished", finished);
        if (rows.getChildCount() == 0 && !tracks.isEmpty())
            rows.addView(label("You’ve finished everything in this library.", 15, MUTED, false));
        if (!busy && status.getText().length() == 0 && tracks.isEmpty() && !needsSignIn && app.settings() != null)
            status.setText("Your listening folder has no MP3s.");
    }

    private void group(String name, java.util.List<ListeningApp.Track> tracks) {
        if (tracks.isEmpty()) return;
        space(rows, 10);
        rows.addView(label(name, 14, MUTED, true));
        for (var track : tracks) {
            space(rows, 6);
            LinearLayout item = row();
            item.setPadding(dp(12), dp(7), dp(7), dp(7));
            item.setBackground(card(0xfffafaf4, LINE, 12));
            LinearLayout details = column();
            details.addView(label(track.episode().title(), 16, INK, true));
            var bookmark = app.bookmark(track.key());
            long duration = bookmark == null ? track.episode().durationMs() : bookmark.durationMs();
            if (bookmark != null && !bookmark.finished())
                details.addView(label("At " + time(bookmark.positionMs()) +
                    (duration > 0 ? " · " + time(duration) + " total" : ""), 13, MUTED, false));
            else if (duration > 0) details.addView(label(time(duration), 13, MUTED, false));
            item.addView(details, weight());
            Button play = button(action(track), GREEN, PAPER);
            play.setContentDescription(action(track) + " " + track.episode().title());
            play.setEnabled(player != null);
            play.setOnClickListener(v -> toggle(track));
            item.addView(play);
            rows.addView(item);
        }
    }

    private void updateActions() {
        connect.setVisibility(app.settings() == null || needsSignIn ? View.VISIBLE : View.GONE);
        connect.setEnabled(!busy);
        refresh.setEnabled(!busy && app.settings() != null);
        status.setVisibility(status.getText().length() == 0 ? View.GONE : View.VISIBLE);
    }

    private EditText field(LinearLayout content, String label, String value) {
        var field = new EditText(this);
        field.setHint(label);
        field.setText(value);
        field.setSingleLine(true);
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        content.addView(field);
        return field;
    }

    private static String value(EditText field) { return field.getText().toString(); }

    private LinearLayout column() {
        var layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        return layout;
    }

    private LinearLayout row() {
        var layout = new LinearLayout(this);
        layout.setGravity(Gravity.CENTER_VERTICAL);
        layout.setOrientation(LinearLayout.HORIZONTAL);
        return layout;
    }

    private LinearLayout.LayoutParams weight() { return new LinearLayout.LayoutParams(0, -2, 1f); }

    private TextView label(String text, int size, int color, boolean bold) {
        var view = new TextView(this);
        view.setText(text);
        view.setTextColor(color);
        view.setTextSize(size);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private Button button(String text, int color, int background) {
        var view = new Button(this);
        view.setText(text);
        view.setAllCaps(false);
        view.setTextColor(color);
        view.setMinHeight(dp(48));
        view.setMinimumWidth(0);
        view.setPadding(dp(12), dp(8), dp(12), dp(8));
        var fill = card(background, background, 10);
        view.setBackground(new android.graphics.drawable.RippleDrawable(
            ColorStateList.valueOf(0x223e6352), fill, null));
        return view;
    }

    private GradientDrawable card(int fill, int line, int radius) {
        var background = new GradientDrawable();
        background.setColor(fill);
        background.setCornerRadius(dp(radius));
        background.setStroke(dp(1), line);
        return background;
    }

    private void space(LinearLayout parent, int dp) { parent.addView(new View(this), -1, dp(dp)); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private static String time(long millis) {
        long seconds = Math.max(0, millis / 1000);
        return seconds < 3600 ? String.format(Locale.ROOT, "%d:%02d", seconds / 60, seconds % 60)
            : String.format(Locale.ROOT, "%d:%02d:%02d", seconds / 3600, seconds / 60 % 60, seconds % 60);
    }
}
