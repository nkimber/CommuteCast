package dev.kimber.commutecast.listener;

import android.app.PendingIntent;
import android.content.Intent;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;

import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.ForwardingSimpleBasePlayer;
import androidx.media3.common.MediaItem;
import androidx.media3.common.Player;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.datasource.DefaultHttpDataSource;
import androidx.media3.datasource.ResolvingDataSource;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
import androidx.media3.session.MediaSession;
import androidx.media3.session.MediaSessionService;

import com.google.common.util.concurrent.Futures;
import com.google.common.util.concurrent.ListenableFuture;
import com.google.common.util.concurrent.SettableFuture;

import java.io.IOException;
import java.util.Collections;
import java.util.List;

@UnstableApi
public final class PlaybackService extends MediaSessionService {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private ListeningApp app;
    private ExoPlayer player;
    private MediaSession session;
    private String playingKey;
    private long playedAt;
    private boolean rewindPending;

    private final Runnable timer = new Runnable() {
        @Override public void run() {
            if (player.isPlaying()) checkpoint(false);
            handler.postDelayed(this, 5_000);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        app = (ListeningApp) getApplication();
        var http = new DefaultHttpDataSource.Factory().setConnectTimeoutMs(15_000).setReadTimeoutMs(20_000);
        var source = new ResolvingDataSource.Factory(http, spec -> {
            if (!"commutecast".equals(spec.uri.getScheme()) || !"onedrive".equals(spec.uri.getHost()))
                throw new IOException("The source is outside the listening library.");
            try {
                Uri media = Uri.parse(app.playbackUri(spec.uri.getLastPathSegment()).toString());
                return spec.withUri(media);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new IOException("Opening the episode was interrupted.");
            } catch (Exception e) {
                throw new IOException(MicrosoftSignIn.issue(e));
            }
        });
        player = new ExoPlayer.Builder(this).setMediaSourceFactory(new DefaultMediaSourceFactory(source))
            .setAudioAttributes(new AudioAttributes.Builder().setUsage(C.USAGE_MEDIA)
                .setContentType(C.AUDIO_CONTENT_TYPE_SPEECH).build(), true)
            .setHandleAudioBecomingNoisy(true).setWakeMode(C.WAKE_MODE_LOCAL)
            .setSeekBackIncrementMs(ListeningApp.Bookmark.REWIND_MS).build();
        player.addListener(new Player.Listener() {
            @Override public void onMediaItemTransition(MediaItem item, int reason) {
                playingKey = item == null ? null : item.mediaId;
                playedAt = 0;
                rewindPending = false;
            }

            @Override public void onIsPlayingChanged(boolean isPlaying) {
                if (isPlaying) {
                    if (playedAt == 0 || rewindPending) playedAt = System.currentTimeMillis();
                    rewindPending = false;
                    checkpoint(false);
                } else if (player.getPlaybackState() != Player.STATE_ENDED) {
                    checkpoint(false);
                }
            }

            @Override public void onPlaybackStateChanged(int state) {
                if (state == Player.STATE_ENDED) {
                    checkpoint(true);
                    playedAt = 0;
                    rewindPending = false;
                }
            }

            @Override public void onPositionDiscontinuity(Player.PositionInfo oldPosition,
                    Player.PositionInfo newPosition, int reason) {
                if (reason == Player.DISCONTINUITY_REASON_SEEK && !rewindPending) checkpoint(false);
            }
        });

        var controlledPlayer = new ForwardingSimpleBasePlayer(player) {
            @Override protected ListenableFuture<?> handleSetPlayWhenReady(boolean ready) {
                if (!ready) {
                    checkpoint(false);
                    rewindPending = playedAt != 0;
                } else if (!player.getPlayWhenReady() || player.getPlaybackState() == Player.STATE_ENDED ||
                        player.getPlaybackState() == Player.STATE_IDLE) {
                    if (player.getPlaybackState() == Player.STATE_ENDED) player.seekTo(0);
                    else if (rewindPending && playedAt != 0) {
                        long target = Math.max(0, player.getCurrentPosition() - ListeningApp.Bookmark.REWIND_MS);
                        player.seekTo(target);
                    }
                    if (player.getPlaybackState() == Player.STATE_IDLE) player.prepare();
                }
                return super.handleSetPlayWhenReady(ready);
            }

            @Override protected ListenableFuture<?> handleSetMediaItems(List<MediaItem> items, int index, long positionMs) {
                checkpoint(false);
                return super.handleSetMediaItems(items, index, positionMs);
            }
        };
        Intent open = new Intent(this, ListenerActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent launch = PendingIntent.getActivity(this, 0, open, PendingIntent.FLAG_IMMUTABLE);
        session = new MediaSession.Builder(this, controlledPlayer).setSessionActivity(launch)
            .setCallback(new SessionCallbacks()).build();
        handler.postDelayed(timer, 5_000);
    }

    @Override public MediaSession onGetSession(MediaSession.ControllerInfo controller) {
        return controller.getPackageName().equals(getPackageName()) || controller.isTrusted() ? session : null;
    }

    private void checkpoint(boolean finished) {
        if (playingKey == null || playedAt == 0 ||
            !finished && player.getPlaybackState() == Player.STATE_ENDED) return;
        long duration = Math.max(0, player.getDuration());
        if (duration == 0) {
            var previous = app.bookmark(playingKey);
            var track = app.track(playingKey);
            duration = previous != null ? previous.durationMs() : track == null ? 0 : track.episode().durationMs();
        }
        long position = finished && duration > 0 ? duration : Math.max(0, player.getCurrentPosition());
        app.store(playingKey, new ListeningApp.Bookmark(position, duration, playedAt, finished));
    }

    @Override public void onDestroy() {
        handler.removeCallbacks(timer);
        checkpoint(false);
        session.release();
        player.release();
        super.onDestroy();
    }

    private final class SessionCallbacks implements MediaSession.Callback {
        @Override public MediaSession.ConnectionResult onConnect(MediaSession session, MediaSession.ControllerInfo controller) {
            if (controller.getPackageName().equals(getPackageName()))
                return MediaSession.Callback.super.onConnect(session, controller);
            if (!controller.isTrusted()) return MediaSession.ConnectionResult.reject();
            var commands = new Player.Commands.Builder()
                .addAll(MediaSession.ConnectionResult.DEFAULT_PLAYER_COMMANDS)
                .remove(Player.COMMAND_SET_MEDIA_ITEM).remove(Player.COMMAND_CHANGE_MEDIA_ITEMS)
                .build();
            return new MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                .setAvailablePlayerCommands(commands).build();
        }

        @Override public ListenableFuture<List<MediaItem>> onAddMediaItems(MediaSession session,
                MediaSession.ControllerInfo controller, List<MediaItem> items) {
            if (!controller.getPackageName().equals(getPackageName()) || items.size() != 1)
                return Futures.immediateFailedFuture(new IllegalArgumentException("Choose an episode from the library."));
            ListeningApp.Track track = app.track(items.get(0).mediaId);
            if (track == null)
                return Futures.immediateFailedFuture(new IllegalArgumentException("Refresh the OneDrive library."));
            return Futures.immediateFuture(Collections.singletonList(track.mediaItem()));
        }

        @Override public ListenableFuture<MediaSession.MediaItemsWithStartPosition> onPlaybackResumption(
                MediaSession session, MediaSession.ControllerInfo controller, boolean isForPlayback) {
            var result = SettableFuture.<MediaSession.MediaItemsWithStartPosition>create();
            app.executor().execute(() -> {
                try {
                    if (isForPlayback) app.refresh();
                    var track = app.latest();
                    if (track == null) throw new IOException("Choose an episode from CommuteCast.");
                    var bookmark = app.bookmark(track.key());
                    result.set(new MediaSession.MediaItemsWithStartPosition(
                        Collections.singletonList(track.mediaItem()), 0, bookmark.resumeAt()));
                } catch (Exception e) {
                    result.setException(new IllegalStateException(MicrosoftSignIn.issue(e)));
                }
            });
            return result;
        }
    }
}
