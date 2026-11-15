"use strict";

const rewindSeconds = 20;
const checkpointMs = 5000;
const bookmarkPrefix = "commutecast.listen.v1.";
const byId = id => document.getElementById(id);
const audio = byId("audio");
const showFinished = byId("show-finished");
const seek = byId("seek");
const list = byId("episodes");
const mediaSession = navigator.mediaSession;
const bookmarks = new Map();
let catalog = null;
let current = null;
let refreshError = "";
let storageAvailable = true;

function readBookmark(key) {
    try {
        const value = JSON.parse(localStorage.getItem(bookmarkPrefix + key));
        return value && Number.isFinite(value.position) && value.position >= 0 &&
            Number.isFinite(value.playedAt) ? value : null;
    } catch {
        return null;
    }
}

function saveBookmark(finished = false) {
    if (!current?.playedAt || !finished && audio.ended) return;
    const key = current.episode.key;
    const old = bookmarks.get(key);
    const duration = mediaDuration() ?? old?.duration ?? current.episode.durationSeconds;
    const position = finished ? duration ?? audio.currentTime : audio.currentTime;
    if (!Number.isFinite(position) || position < 0) return;
    const value = { position, duration, playedAt: current.playedAt, finished };
    current.lastCheckpoint = performance.now();
    bookmarks.set(key, value);
    try {
        if (storageAvailable) localStorage.setItem(bookmarkPrefix + key, JSON.stringify(value));
    } catch {
        storageAvailable = false;
        byId("storage-warning").hidden = false;
    }
}

function mediaDuration() {
    return Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : null;
}

function timeLabel(seconds) {
    const whole = Math.max(0, Math.floor(seconds));
    const hours = Math.floor(whole / 3600);
    const minutes = Math.floor(whole / 60) % 60;
    const remainder = String(whole % 60).padStart(2, "0");
    return hours ? hours + ":" + String(minutes).padStart(2, "0") + ":" + remainder : minutes + ":" + remainder;
}

function continueEpisode() {
    const episodes = catalog?.episodes ?? [];
    let latest = null;
    for (const episode of episodes) {
        const saved = bookmarks.get(episode.key);
        if (saved && !saved.finished && (!latest || saved.playedAt > bookmarks.get(latest.key).playedAt))
            latest = episode;
    }
    return latest;
}

function playerEpisode() {
    return current?.episode ?? continueEpisode();
}

function actionLabel(episode) {
    if (current?.episode.key === episode.key) {
        if (current.phase === "loading") return "Cancel";
        if (current.phase === "playing") return "Pause";
        if (current.phase === "failed") return "Retry";
    }
    const saved = bookmarks.get(episode.key);
    return saved?.finished ? "Replay" : saved ? "Resume" : "Play";
}

function renderTimeline() {
    const episode = playerEpisode();
    if (!episode) return;
    const saved = bookmarks.get(episode.key);
    const canSeek = current?.playedAt && mediaDuration() && !audio.error;
    const position = canSeek ? audio.currentTime : saved?.position ?? 0;
    const duration = (canSeek ? mediaDuration() : saved?.duration) ?? episode.durationSeconds;
    seek.max = String(duration ?? 1);
    seek.value = String(position);
    seek.disabled = !canSeek;
    seek.setAttribute("aria-valuetext", timeLabel(position));
    byId("rewind").disabled = !canSeek || position <= 0;
    byId("position").textContent = timeLabel(position);
    byId("remaining").textContent = duration ? timeLabel(Math.max(0, duration - position)) + " left" : "";

    if (canSeek && mediaSession && typeof mediaSession.setPositionState === "function")
        mediaSession.setPositionState({ duration, position: Math.min(position, duration), playbackRate: audio.playbackRate });
}

function renderPlayer() {
    const episode = playerEpisode();
    byId("player-empty").hidden = Boolean(episode);
    byId("player-content").hidden = !episode;
    if (!episode) return;
    byId("player-title").textContent = episode.title;
    byId("play-toggle").textContent = actionLabel(episode);
    renderTimeline();
}

function renderLibrary() {
    const episodes = catalog?.episodes ?? [];
    byId("setup-link").hidden = catalog?.configured !== false;
    const focusedKey = document.activeElement?.dataset.episode;
    const entries = episodes.map(episode => ({ episode, saved: bookmarks.get(episode.key) }));
    const inProgress = entries.filter(entry => entry.saved && !entry.saved.finished)
        .sort((a, b) => b.saved.playedAt - a.saved.playedAt);
    const unplayed = entries.filter(entry => !entry.saved);
    const finished = entries.filter(entry => entry.saved?.finished);
    const groups = [["In progress", inProgress], ["Unplayed", unplayed]];
    if (showFinished.checked) groups.push(["Finished", finished]);

    const sections = document.createDocumentFragment();
    let focusedReplacement = null;
    for (const [label, entries] of groups) {
        if (!entries.length) continue;
        const group = document.createElement("section");
        group.className = "episode-group";
        const heading = document.createElement("h3");
        heading.textContent = label;
        const rows = document.createElement("ul");
        rows.className = "episode-list";
        for (const { episode, saved } of entries) {
            const row = document.createElement("li");
            row.className = "episode-row";
            const details = document.createElement("div");
            const title = document.createElement("strong");
            title.textContent = episode.title;
            details.append(title);
            const duration = saved?.duration ?? episode.durationSeconds;
            if (duration || saved && !saved.finished) {
                const timing = document.createElement("small");
                timing.textContent = saved && !saved.finished
                    ? "At " + timeLabel(saved.position) + (duration ? " · " + timeLabel(duration) + " total" : "")
                    : timeLabel(duration);
                details.append(timing);
            }
            const button = document.createElement("button");
            button.type = "button";
            button.className = "secondary";
            button.dataset.episode = episode.key;
            button.textContent = actionLabel(episode);
            button.setAttribute("aria-label", button.textContent + " " + episode.title);
            if (episode.key === focusedKey) focusedReplacement = button;
            row.append(details, button);
            rows.append(row);
        }
        group.append(heading, rows);
        sections.append(group);
    }
    list.replaceChildren(sections);
    if (focusedKey) (focusedReplacement ?? showFinished).focus({ preventScroll: true });
    byId("library-status").textContent = refreshError ||
        (catalog?.configured === false ? "Choose the Microsoft 365 folder that holds your MP3s."
            : !catalog ? "Loading your library…"
            : !episodes.length ? "Your listening folder has no MP3s yet."
            : !list.childElementCount ? "You’ve finished everything in this library."
            : "");
}

function render() {
    renderPlayer();
    renderLibrary();
}

function releaseAudio() {
    current = null;
    audio.pause();
    audio.removeAttribute("src");
    audio.load();
    if (mediaSession) {
        mediaSession.metadata = null;
        mediaSession.playbackState = "none";
        mediaSession.setPositionState?.();
    }
}

function playbackFailed(session) {
    if (current !== session || !session || session.phase === "failed") return;
    if (session.playedAt) saveBookmark();
    session.phase = "failed";
    session.playedAt = null;
    audio.pause();
    if (mediaSession) mediaSession.playbackState = "paused";
    byId("player-notice").textContent = "Couldn’t play this MP3. Tap Retry, or refresh the library if the file moved.";
    render();
}

function startPlayback(episode) {
    saveBookmark();
    const saved = bookmarks.get(episode.key);
    const start = saved && !saved.finished ? Math.max(0, saved.position - rewindSeconds) : 0;
    releaseAudio();
    const session = { episode, phase: "loading", start, playedAt: null, lastCheckpoint: 0 };
    current = session;
    byId("player-notice").textContent = "Opening episode…";
    audio.src = "/api/listen/media/" + encodeURIComponent(episode.id) + "/" + encodeURIComponent(episode.key);
    audio.currentTime = start;
    if (mediaSession && typeof MediaMetadata === "function")
        mediaSession.metadata = new MediaMetadata({ title: episode.title, album: "CommuteCast",
            artwork: [{ src: "/listen-512.png", sizes: "512x512", type: "image/png" }] });
    audio.play().catch(() => playbackFailed(session));
    render();
}

function togglePlayback(episode) {
    if (episode.key === current?.episode.key && current.phase === "loading") {
        releaseAudio();
        byId("player-notice").textContent = "";
        render();
    } else if (episode.key === current?.episode.key && current.phase === "playing") {
        audio.pause();
    } else {
        startPlayback(episode);
    }
}

function seekTo(position) {
    const duration = mediaDuration();
    if (current?.playedAt && duration && Number.isFinite(position))
        audio.currentTime = Math.min(duration, Math.max(0, position));
}

audio.addEventListener("loadedmetadata", () => {
    const duration = mediaDuration();
    if (current?.phase === "loading" && duration && current.start >= duration) audio.currentTime = 0;
});
audio.addEventListener("playing", () => {
    if (current?.phase !== "loading" || audio.paused) return;
    current.phase = "playing";
    current.playedAt = Date.now();
    if (mediaSession) mediaSession.playbackState = "playing";
    byId("player-notice").textContent = "";
    saveBookmark();
    render();
});
audio.addEventListener("timeupdate", () => {
    if (current?.phase !== "playing") return;
    if (!audio.seeking && performance.now() - current.lastCheckpoint >= checkpointMs) saveBookmark();
    renderTimeline();
});
audio.addEventListener("seeked", () => {
    if (!current?.playedAt) return;
    saveBookmark();
    renderTimeline();
});
audio.addEventListener("pause", () => {
    if (!current || !audio.paused || audio.ended) return;
    if (current.phase === "loading" && audio.error === null) {
        releaseAudio();
        byId("player-notice").textContent = "";
    } else if (current.phase === "playing") {
        current.phase = "paused";
        saveBookmark();
        if (mediaSession) mediaSession.playbackState = "paused";
    }
    render();
});
audio.addEventListener("ended", () => {
    if (!current?.playedAt || !audio.ended) return;
    const title = current.episode.title;
    saveBookmark(true);
    releaseAudio();
    byId("player-notice").textContent = "Finished " + title + ".";
    render();
});
audio.addEventListener("error", () => {
    if (audio.error) playbackFailed(current);
});

async function loadLibrary() {
    const refresh = byId("refresh");
    if (refresh.disabled) return;
    refresh.disabled = true;
    try {
        const response = await fetch("/api/listen/library", { headers: { Accept: "application/json" } });
        if (!response.ok || !response.headers.get("content-type")?.includes("application/json")) throw new Error();
        catalog = await response.json();
        for (const episode of catalog.episodes)
            if (!bookmarks.has(episode.key)) bookmarks.set(episode.key, readBookmark(episode.key));
        refreshError = "";
    } catch {
        refreshError = "Can’t load the library. Check your connection and sign-in, then tap Refresh.";
    } finally {
        refresh.disabled = false;
        render();
    }
}

byId("play-toggle").addEventListener("click", () => {
    const episode = playerEpisode();
    if (episode) togglePlayback(episode);
});
byId("rewind").addEventListener("click", () => seekTo(audio.currentTime - rewindSeconds));
seek.addEventListener("input", () => seekTo(seek.valueAsNumber));
list.addEventListener("click", event => {
    const key = event.target.closest("button")?.dataset.episode;
    const episode = catalog?.episodes.find(item => item.key === key);
    if (episode) togglePlayback(episode);
});
showFinished.addEventListener("change", renderLibrary);
byId("refresh").addEventListener("click", loadLibrary);
window.addEventListener("pagehide", () => saveBookmark());
document.addEventListener("visibilitychange", () => {
    if (document.hidden) saveBookmark();
    else loadLibrary();
});

if (mediaSession) {
    const actions = {
        play: () => {
            const episode = playerEpisode();
            if (episode && audio.paused) startPlayback(episode);
        },
        pause: () => audio.pause(),
        stop: () => audio.pause(),
        seekbackward: event => seekTo(audio.currentTime - (event.seekOffset ?? rewindSeconds)),
        seekforward: event => seekTo(audio.currentTime + (event.seekOffset ?? rewindSeconds)),
        seekto: event => seekTo(event.seekTime)
    };
    for (const [action, handler] of Object.entries(actions)) {
        try { mediaSession.setActionHandler(action, handler); }
        catch { /* Some devices expose only a subset of the controls. */ }
    }
}

loadLibrary();
