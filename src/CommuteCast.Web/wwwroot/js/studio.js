"use strict";
const $ = (id) => document.getElementById(id);
const csrf = document.querySelector('meta[name="csrf-token"]').content;
let state,
  draftRevision = "",
  draftTimer,
  saveChain = Promise.resolve(),
  ready = false,
  filter = "all",
  selectedId = null;
let promptGeneratedFor = null,
  promptVersion = 0,
  cast = [],
  profile = null;
let submissionId = crypto.randomUUID().replaceAll("-", "");
const pageNames = {
  new: "New narration",
  library: "Your library",
  prompt: "Prompt builder",
  settings: "Settings",
};
function node(tag, className, text) {
  const n = document.createElement(tag);
  if (className) n.className = className;
  if (text !== undefined) n.textContent = text;
  return n;
}
function icon(id) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  const use = document.createElementNS(svg.namespaceURI, "use");
  use.setAttribute("href", "#i-" + id);
  svg.append(use);
  return svg;
}
function message(text) {
  $("global-error").textContent = text;
  $("global-error").hidden = !text;
}
let toastTimer;
function toast(text) {
  $("toast").textContent = text;
  $("toast").hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ($("toast").hidden = true), 4500);
}
async function api(path, body) {
  const response = await fetch("/api/" + path, {
    method: body === undefined ? "GET" : "POST",
    headers:
      body === undefined
        ? {}
        : { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: "same-origin",
    cache: "no-store",
  });
  if (!response.ok) {
    let text =
      response.status === 401 || response.status === 403
        ? "Your sign-in has expired. Copy any unsaved text, then refresh to sign in again."
        : "The server could not finish the request.";
    try {
      const error = await response.json();
      text = error.error || text;
    } catch {
      /* Keep the useful fallback. */
    }
    throw new Error(text);
  }
  return response.headers.get("content-type")?.includes("json")
    ? response.json()
    : null;
}
async function action(button, work) {
  const previous = button?.disabled;
  if (button) button.disabled = true;
  message("");
  try {
    return await work();
  } catch (error) {
    message(error.message || "The operation could not finish.");
    return undefined;
  } finally {
    if (button) button.disabled = previous || false;
  }
}
function bind(id, work) {
  $(id).addEventListener("click", (event) => action(event.currentTarget, work));
}
function navigate() {
  const page =
    location.hash.slice(1) in pageNames ? location.hash.slice(1) : "new";
  for (const key of Object.keys(pageNames))
    $("page-" + key).hidden = key !== page;
  document.querySelectorAll("[data-page]").forEach((a) => {
    const active = a.dataset.page === page;
    a.classList.toggle("active", active);
    if (active) a.setAttribute("aria-current", "page");
    else a.removeAttribute("aria-current");
  });
  $("page-name").textContent = pageNames[page];
  document.title = pageNames[page] + " · CommuteCast";
}
window.addEventListener("hashchange", () => {
  navigate();
  window.scrollTo(0, 0);
});
navigate();
function options(select, choices, selected) {
  select.replaceChildren(
    ...choices.map((v) => {
      const o = new Option(v.displayName || v.name || v.id, v.id);
      return o;
    }),
  );
  if (selected && choices.some((c) => c.id === selected))
    select.value = selected;
}
function provider() {
  return state.providers.find((p) => p.id === $("engine").value);
}
function updateCapabilities() {
  const p = provider();
  if (!p) return;
  const model = $("model").value;
  const delivery =
    p.capabilities.deliveryInstructions &&
    !(p.id === "openai" && ["tts-1", "tts-1-hd"].includes(model));
  const emotion =
    p.capabilities.emotion &&
    !(p.id === "elevenlabs" && !["eleven_v3", "eleven_v4"].includes(model));
  $("model-section").hidden = !p.capabilities.hosted;
  $("delivery-section").hidden = !delivery;
  $("emotion-section").hidden = !emotion;
  $("local-controls").hidden = p.capabilities.hosted;
  $("speed").max =
    p.id === "elevenlabs" ? "1.2" : p.id === "gemini" ? "1" : "1.4";
  $("speed").min = p.id === "gemini" ? "1" : "0.7";
  if (p.id === "gemini") $("speed").value = "1";
  if (+$("speed").value > +$("speed").max) $("speed").value = $("speed").max;
  $("blend").disabled = p.id !== "kokoro";
  updateCounts();
}
function refreshComposeVoices(selectedVoice, selectedModel) {
  const p = provider();
  if (!p) return;
  options(
    $("model"),
    p.capabilities.models.map((id) => ({ id })),
    selectedModel || p.defaultModel,
  );
  options($("voice"), p.voices, selectedVoice || p.defaultVoice);
  options(
    $("blend"),
    [{ id: "", displayName: "No blend" }, ...p.voices],
    $("blend").value,
  );
  $("voice-note").textContent = p.verified
    ? p.voices.filter((v) => v.available).length +
      " verified voices · " +
      (p.capabilities.hosted
        ? "Hosted speech may be billed."
        : "Generated on your micro PC.")
    : "Refresh voices to check the connection and available narrators.";
  updateCapabilities();
  renderCast();
}
function captureOptions() {
  const p = provider();
  if (!p) throw new Error("Wait for the studio to connect.");
  return {
    engine: p.id,
    voice: $("voice").value,
    speed: +$("speed").value,
    excludeCode: $("exclude-code").checked,
    pronunciation: $("pronunciation").value,
    profile: profile || {
      version: "pronunciation-v2",
      dictionaryVersion: "literal-dictionary-v1",
      language: "en",
      numbers: "AsWritten",
      acronyms: "AsWritten",
      dates: "NoCalendarInterpretation",
    },
    localVoice: p.capabilities.hosted
      ? null
      : {
          naturalPhrasing: $("natural").checked,
          blendVoice: p.id === "kokoro" ? $("blend").value : "",
          blendWeight: +$("blend-weight").value,
          noiseScale: null,
          noiseWidth: null,
          gainDb: +$("gain").value,
          sentencePauseMs: 220,
          paragraphPauseMs: 500,
          turnPauseMs: 140,
          version: 1,
        },
    speech: p.capabilities.hosted
      ? {
          model: $("model").value,
          delivery: $("delivery-section").hidden ? "" : $("delivery").value,
          emotion: $("emotion-section").hidden ? "" : $("emotion").value,
          dialogue: true,
        }
      : null,
  };
}
function applyOptions(o) {
  if (!o) return;
  profile = o.profile;
  $("engine").value = o.engine;
  refreshComposeVoices(o.voice, o.speech?.model);
  $("speed").value = o.speed;
  $("exclude-code").checked = o.excludeCode;
  $("pronunciation").value = o.pronunciation;
  $("natural").checked = o.localVoice?.naturalPhrasing ?? true;
  $("blend").value = o.localVoice?.blendVoice || "";
  $("blend-weight").value = o.localVoice?.blendWeight ?? 0.25;
  $("gain").value = o.localVoice?.gainDb || 0;
  $("delivery").value = o.speech?.delivery || "";
  $("emotion").value = o.speech?.emotion || "";
  updateCounts();
}
function captureEpisode() {
  if (!$("podcast-mode").checked) return null;
  return {
    format: {
      id: "web-conversation",
      name: "Host and guests",
      hosts: 1,
      minimumGuests: 1,
      maximumGuests: 4,
      structure:
        "A curious host explores ideas with thoughtful guests, building a connected explanation.",
    },
    speakers: cast.map((s) => ({
      ...s,
      localVoice: captureOptions().localVoice,
    })),
    dialogue: true,
    version: 1,
  };
}
function composeRequest() {
  return {
    title: $("title").value,
    source: $("source").value,
    options: captureOptions(),
    generationPrompt: $("generation-prompt").value || null,
    episode: captureEpisode(),
    submissionId,
  };
}
function renderCast() {
  const host = $("cast-editors");
  host.replaceChildren();
  cast.forEach((speaker, i) => {
    const box = node("div", "cast-editor");
    const title = node("div", "label-row");
    title.append(node("strong", "", i === 0 ? "Host" : "Guest " + i));
    if (i > 1) {
      const remove = node("button", "text-button", "Remove");
      remove.type = "button";
      remove.onclick = () => {
        cast.splice(i, 1);
        renderCast();
        scheduleDraft();
      };
      title.append(remove);
    }
    box.append(title);
    const nameId = "cast-name-" + i,
      voiceId = "cast-voice-" + i;
    const label = node("label", "", "Speaker name");
    label.htmlFor = nameId;
    const name = node("input");
    name.id = nameId;
    name.value = speaker.name;
    name.maxLength = 40;
    name.oninput = () => {
      speaker.name = name.value;
      scheduleDraft();
    };
    const voiceLabel = node("label", "", "Voice");
    voiceLabel.htmlFor = voiceId;
    const voice = node("select");
    voice.id = voiceId;
    options(voice, provider()?.voices || [], speaker.voice);
    voice.onchange = () => {
      speaker.voice = voice.value;
      scheduleDraft();
    };
    box.append(label, name, voiceLabel, voice);
    host.append(box);
  });
  $("add-speaker").disabled = cast.length >= 5;
}
function addSpeaker() {
  const voices = provider()?.voices || [];
  cast.push({
    name: cast.length ? "Guest" + (cast.length > 1 ? cast.length : "") : "Host",
    role: cast.length ? "Guest" : "Host",
    expertise: "",
    style: "Thoughtful and conversational",
    voice:
      voices[cast.length % Math.max(voices.length, 1)]?.id || $("voice").value,
    speed: 1,
    delivery: "",
    emotion: "",
  });
  renderCast();
  scheduleDraft();
}
function brief() {
  return {
    topic: $("prompt-topic").value,
    goal: $("prompt-goal").value,
    audience: $("prompt-audience").value,
    minutes: $("prompt-minutes").value,
    style: $("prompt-style").value,
    tone: $("prompt-tone").value,
    include: $("prompt-include").value,
    avoid: $("prompt-avoid").value,
    sourceMaterial: $("prompt-sources").value,
    evidence: $("prompt-supplied").checked ? "SuppliedOnly" : "Research",
  };
}
function promptFresh() {
  return (
    !!$("prompt-output").value &&
    promptVersion === 2 &&
    JSON.stringify(brief()) === JSON.stringify(promptGeneratedFor)
  );
}
function updatePromptState() {
  const fresh = promptFresh();
  $("copy-prompt").disabled = !fresh;
  $("use-prompt").disabled = !fresh;
  $("prompt-state").textContent = fresh
    ? "Ready to use"
    : $("prompt-output").value
      ? "Brief changed · rebuild"
      : "Ready when you are";
}
function captureDraft() {
  return {
    title: $("title").value,
    source: $("source").value,
    generationPrompt: $("generation-prompt").value || null,
    promptDraft: {
      brief: brief(),
      prompt: $("prompt-output").value,
      generatedFor: promptGeneratedFor,
      templateVersion: promptVersion,
    },
    podcast: $("podcast-mode").checked
      ? { enabled: true, episode: captureEpisode() }
      : null,
    version: null,
  };
}
function scheduleDraft() {
  if (!ready) return;
  clearTimeout(draftTimer);
  $("draft-status").textContent = "Saving…";
  draftTimer = setTimeout(saveDraft, 650);
}
function saveDraft() {
  if (!ready) return saveChain;
  const draft = captureDraft();
  saveChain = saveChain
    .then(async () => {
      try {
        const saved = await api("draft", { draft, revision: draftRevision });
        draftRevision = saved.revision;
        $("draft-status").textContent = "Draft saved";
      } catch (error) {
        $("draft-status").textContent = "Draft not saved";
        message(error.message);
        throw error;
      }
    })
    .catch(() => {
      /* Keep later local edits available for retry; conflicts remain explicit. */
    });
  return saveChain;
}
function loadDraft(draft) {
  $("title").value = draft.title;
  $("source").value = draft.source;
  $("generation-prompt").value = draft.generationPrompt || "";
  const p = draft.promptDraft;
  if (p) {
    const b = p.brief;
    for (const [id, key] of Object.entries({
      "prompt-topic": "topic",
      "prompt-goal": "goal",
      "prompt-audience": "audience",
      "prompt-minutes": "minutes",
      "prompt-style": "style",
      "prompt-tone": "tone",
      "prompt-include": "include",
      "prompt-avoid": "avoid",
      "prompt-sources": "sourceMaterial",
    }))
      $(id).value = b[key];
    $("prompt-supplied").checked = b.evidence === "SuppliedOnly";
    $("prompt-output").value = p.prompt;
    promptVersion = p.templateVersion;
    // Normalize property order for comparison across persisted JSON serializers.
    promptGeneratedFor = p.generatedFor
      ? Object.fromEntries(
          Object.keys(brief()).map((k) => [k, p.generatedFor[k]]),
        )
      : null;
  }
  if (draft.podcast?.enabled) {
    $("podcast-mode").checked = true;
    $("cast-section").hidden = false;
    cast = draft.podcast.episode.speakers;
    renderCast();
  }
  updateCounts();
  updatePromptState();
}
function updateCounts() {
  const words = $("source").value.trim().split(/\s+/u).filter(Boolean).length;
  $("word-count").textContent = words.toLocaleString() + " words";
  $("duration-estimate").textContent =
    "About " +
    Math.ceil(words / (150 * +$("speed").value)) +
    " min of listening";
  $("speed-label").textContent = (+$("speed").value).toFixed(2) + "×";
}
function working(j) {
  return !["Exported", "Failed", "Cancelled", "Deleting"].includes(j.stage);
}
function attention(j) {
  return (
    j.stage === "Failed" ||
    j.stage === "Cancelled" ||
    j.publication?.status === "Failed" ||
    j.publication?.status === "Not configured"
  );
}
function status(j) {
  const p = j.publication;
  if (j.stage === "Exported")
    return p?.status === "Published"
      ? ["Published", ""]
      : p?.status === "Failed"
        ? ["Publishing failed", "error"]
        : p?.status === "Publishing"
          ? ["Publishing to M365", "warning"]
          : ["Generated", "neutral"];
  return j.stage === "Failed"
    ? ["Generation failed", "error"]
    : j.stage === "Cancelled"
      ? ["Cancelled", "neutral"]
      : j.stage === "Queued"
        ? [state.paused ? "Queue paused" : "Queued", "neutral"]
        : [j.stage === "Synthesizing" ? "Narrating" : j.stage, "warning"];
}
function jobCard(j) {
  const card = node("button", "job-card");
  card.type = "button";
  card.onclick = () => action(null, () => showDetails(j.id));
  const art = node(
    "span",
    "job-art" + (j.publication?.status === "Published" ? "" : " warm"),
  );
  art.append(icon("wave"));
  const body = node("div");
  body.append(node("div", "job-title", j.title));
  const mins = Math.ceil((j.durationSeconds || (j.words / 150) * 60) / 60);
  body.append(
    node(
      "div",
      "job-meta",
      [
        j.engine,
        mins + " min",
        new Date(j.createdUtc).toLocaleDateString(undefined, {
          month: "short",
          day: "numeric",
        }),
      ].join(" · "),
    ),
  );
  if (j.stage === "Synthesizing" && j.chunks) {
    const progress = node("progress", "job-progress");
    progress.max = j.chunks;
    progress.value = j.completedChunks;
    progress.setAttribute(
      "aria-label",
      j.completedChunks + " of " + j.chunks + " chunks",
    );
    body.append(progress);
  }
  const right = node("div", "job-status"),
    [text, color] = status(j);
  right.append(node("span", "badge " + color, text));
  right.append(
    node(
      "small",
      "job-substatus",
      j.stage === "Synthesizing"
        ? j.completedChunks + " of " + j.chunks + " chunks"
        : j.publication?.status === "Published"
          ? "On your listening shelf ↗"
          : j.stage === "Exported"
            ? j.publication?.status || "Not published"
            : j.words.toLocaleString() + " words",
    ),
  );
  card.append(art, body, right);
  return card;
}
function empty(title, text, link) {
  const box = node("div", "empty-state");
  box.append(icon("library"), node("h3", "", title), node("p", "", text));
  if (link) {
    const a = node("a", "text-link", "Create your first narration →");
    a.href = "#new";
    box.append(a);
  }
  return box;
}
function renderJobs() {
  const jobs = state.jobs;
  $("nav-count").textContent = jobs.length;
  $("stat-total").textContent = jobs.length;
  $("stat-queued").textContent = jobs.filter(working).length;
  $("stat-published").textContent = jobs.filter(
    (j) => j.publication?.status === "Published",
  ).length;
  $("pause-queue").textContent = state.paused ? "Resume queue" : "Pause queue";
  const query = $("library-search").value.toLocaleLowerCase();
  const filtered = jobs.filter(
    (j) =>
      j.title.toLocaleLowerCase().includes(query) &&
      (filter === "all" ||
        (filter === "working" && working(j)) ||
        (filter === "published" && j.publication?.status === "Published") ||
        (filter === "attention" && attention(j))),
  );
  $("library-jobs").replaceChildren(
    ...(filtered.length
      ? filtered.map(jobCard)
      : [
          empty(
            jobs.length
              ? "Nothing here just yet."
              : "A little room for curiosity.",
            jobs.length
              ? "Try a different search or filter."
              : "Your narrations will appear here as you turn good reads into good listens.",
            !jobs.length,
          ),
        ]),
  );
  $("recent-jobs").replaceChildren(
    ...(jobs.length
      ? jobs.slice(0, 3).map(jobCard)
      : [
          empty(
            "Your next listen is waiting to happen.",
            "Start with a story, an idea, or something you’ve been meaning to read.",
            false,
          ),
        ]),
  );
  if (selectedId && $("detail-status")) {
    const j = jobs.find((j) => j.id === selectedId);
    if (j)
      $("detail-status").textContent =
        status(j)[0] + " · " + (j.publication?.status || "Not published");
  }
}
function folderLinks() {
  const p = state.publishing;
  for (const id of ["sidebar-folder", "compose-folder"]) {
    const a = $(id);
    a.href = p.folderUrl || "#settings";
    a.textContent = p.folderUrl
      ? "Open shared folder ↗"
      : "Set up publishing ↗";
    if (p.folderUrl) {
      a.target = "_blank";
      a.rel = "noopener noreferrer";
    } else a.removeAttribute("target");
  }
  $("destination-status").textContent = p.folderUrl
    ? "Connected · " + p.folderName + " · read-only Anyone link verified."
    : p.configured
      ? "Destination saved. Check the folder and its sharing permissions."
      : "Your shared folder isn’t connected yet.";
  $("secret-status").textContent = p.hasSecret
    ? "Application secret saved privately on this server."
    : "No application secret saved yet.";
}
let refreshInFlight = false;
async function refresh() {
  if (refreshInFlight) return;
  refreshInFlight = true;
  try {
    state = await api("state");
    renderJobs();
    folderLinks();
    $("connection").className = "connection connected";
    $("connection").replaceChildren(
      node("span", "status-dot"),
      document.createTextNode("Studio connected"),
    );
    if (state.error) message(state.error);
  } catch (error) {
    $("connection").className = "connection offline";
    $("connection").replaceChildren(
      node("span", "status-dot"),
      document.createTextNode("Reconnecting…"),
    );
    if (!state) message(error.message);
  } finally {
    refreshInFlight = false;
  }
}
function dialog(title, content, eyebrow = "YOUR NARRATION") {
  $("dialog-title").textContent = title;
  $("dialog-eyebrow").textContent = eyebrow;
  $("dialog-content").replaceChildren(content);
  if (!$("detail-dialog").open) $("detail-dialog").showModal();
}
function dialogButton(actions, label, work, danger = false) {
  const b = node(
    "button",
    "button " + (danger ? "danger" : "secondary"),
    label,
  );
  b.type = "button";
  b.onclick = () => action(b, work);
  actions.append(b);
}
async function showDetails(id) {
  const detail = await api("jobs/" + id);
  const j = state.jobs.find((j) => j.id === id);
  if (!j) return;
  selectedId = id;
  const content = node("div"),
    line = node(
      "div",
      "notice",
      status(j)[0] + " · " + (j.publication?.status || "Not published"),
    );
  line.id = "detail-status";
  content.append(line);
  const grid = node("div", "detail-grid");
  for (const [label, value] of [
    ["Provider", detail.settings.engine],
    ["Voice", detail.settings.voice],
    ["Pace", detail.settings.speed + "×"],
    ["Created", new Date(j.createdUtc).toLocaleString()],
  ]) {
    const item = node("div");
    item.append(node("span", "", label), document.createTextNode(value));
    grid.append(item);
  }
  content.append(grid);
  if (j.error || j.publication?.error)
    content.append(node("div", "notice error", j.error || j.publication.error));
  const actions = node("div", "dialog-actions");
  dialogButton(actions, "Use as a new draft", async () => {
    if (
      $("source").value &&
      !confirm("Replace the current narration draft with this saved narration?")
    )
      return;
    $("title").value = detail.title;
    $("source").value = detail.source;
    $("generation-prompt").value = detail.generationPrompt || "";
    applyOptions({
      ...detail.settings,
      profile: detail.settings.profile,
      speech: detail.settings.speech,
    });
    cast = detail.episode?.speakers || [];
    $("podcast-mode").checked = !!detail.episode;
    $("cast-section").hidden = !detail.episode;
    renderCast();
    submissionId = crypto.randomUUID().replaceAll("-", "");
    scheduleDraft();
    $("detail-dialog").close();
    location.hash = "new";
    toast("Saved narration loaded as a new draft.");
  });
  if (["Failed", "Cancelled"].includes(j.stage))
    dialogButton(actions, "Retry generation", async () => {
      await api("jobs/" + id + "/retry", {});
      $("detail-dialog").close();
      await refresh();
      toast("Narration queued for retry.");
    });
  if (working(j))
    dialogButton(actions, "Cancel generation", async () => {
      await api("jobs/" + id + "/cancel", {});
      $("detail-dialog").close();
      await refresh();
    });
  if (j.stage === "Queued") {
    dialogButton(actions, "Move earlier", async () => {
      await api("jobs/" + id + "/earlier", {});
      await refresh();
      toast("Queue order updated.");
    });
    dialogButton(actions, "Move later", async () => {
      await api("jobs/" + id + "/later", {});
      await refresh();
      toast("Queue order updated.");
    });
  }
  if (
    j.stage === "Exported" &&
    j.publication?.status !== "Published" &&
    j.publication?.status !== "Publishing"
  )
    dialogButton(actions, "Publish to Microsoft 365", async () => {
      await api("jobs/" + id + "/publish", {});
      $("detail-dialog").close();
      await refresh();
      toast("Publishing queued. Your finished audio will be reused.");
    });
  if (j.publication?.folderUrl && j.publication?.status === "Published") {
    const a = node("a", "button primary", "Open shared folder ↗");
    a.href = j.publication.folderUrl;
    a.target = "_blank";
    a.rel = "noopener noreferrer";
    actions.append(a);
  }
  dialogButton(
    actions,
    "Delete from studio",
    async () => {
      if (
        !confirm(
          "Delete this narration and its local audio? Any published Microsoft 365 copy will remain in the shared folder.",
        )
      )
        return;
      await api("jobs/" + id + "/delete", {});
      $("detail-dialog").close();
      selectedId = null;
      await refresh();
      toast(
        "Local narration removed. Published files are managed in Microsoft 365.",
      );
    },
    true,
  );
  content.append(actions);
  const script = node("details", "advanced");
  script.append(
    node("summary", "", "Saved narration text"),
    node("div", "dialog-text", detail.source),
  );
  content.append(script);
  dialog(detail.title, content);
}
function settingsProvider() {
  const p = state.providers.find((p) => p.id === $("settings-provider").value);
  if (!p) return;
  $("provider-model-section").hidden = !p.capabilities.hosted;
  $("provider-key-section").hidden = !p.capabilities.hosted;
  options(
    $("settings-model"),
    p.capabilities.models.map((id) => ({ id })),
    p.defaultModel,
  );
  $("provider-key-status").textContent = p.capabilities.hosted
    ? p.hasKey
      ? "API key saved privately on this server."
      : "Add your provider API key to enable this voice service."
    : "No API key needed. Uses the installed local speech service.";
  $("provider-status").textContent = p.verified
    ? "Connection checked. Refresh to check again."
    : "Connection not checked in this workspace.";
}
$("engine").onchange = () => {
  refreshComposeVoices();
  scheduleDraft();
};
$("model").onchange = () => {
  updateCapabilities();
  scheduleDraft();
};
$("voice").onchange = () => scheduleDraft();
$("settings-provider").onchange = settingsProvider;
$("source").oninput = () => {
  updateCounts();
  scheduleDraft();
};
$("speed").oninput = updateCounts;
$("podcast-mode").onchange = () => {
  $("cast-section").hidden = !$("podcast-mode").checked;
  if (!cast.length) {
    addSpeaker();
    addSpeaker();
  }
  scheduleDraft();
};
bind("add-speaker", async () => addSpeaker());
document
  .querySelectorAll(
    "#compose-form input,#compose-form textarea,.voice-panel input,.voice-panel select,.voice-panel textarea",
  )
  .forEach((el) => el.addEventListener("change", scheduleDraft));
$("title").oninput = scheduleDraft;
$("generation-prompt").oninput = scheduleDraft;
document
  .querySelectorAll(
    "#prompt-form input,#prompt-form select,#prompt-form textarea",
  )
  .forEach((el) =>
    el.addEventListener("input", () => {
      updatePromptState();
      scheduleDraft();
    }),
  );
$("prompt-output").oninput = scheduleDraft;
bind("paste-text", async () => {
  try {
    $("source").value = await navigator.clipboard.readText();
    updateCounts();
    scheduleDraft();
  } catch {
    $("source").focus();
    toast("Use Ctrl+V, or touch and hold to paste your text.");
  }
});
bind("refresh-voices", async () => {
  const engine = $("engine").value,
    voice = $("voice").value,
    model = $("model").value;
  await api("providers/check", { engine, model: model || null });
  await refresh();
  refreshComposeVoices(voice, model);
  toast("Connection checked. Voice library refreshed.");
});
bind("save-defaults", async () => {
  await api("defaults", captureOptions());
  await refresh();
  toast("Voice and reading settings saved as your defaults.");
});
bind("preview-script", async () => {
  const result = await api("preview", composeRequest());
  selectedId = null;
  const content = node("div");
  content.append(
    node(
      "p",
      "field-help",
      result.characters.toLocaleString() +
        " spoken characters · " +
        result.changes.length +
        " prepared passages",
    ),
    node("div", "dialog-text", result.script),
  );
  dialog("Review the spoken text", content, "BEFORE IT BECOMES A LISTEN");
});
$("compose-form").onsubmit = (event) => {
  event.preventDefault();
  action($("create-narration"), async () => {
    const request = composeRequest();
    clearTimeout(draftTimer);
    await saveDraft();
    const saved = await api("jobs", request);
    if (
      $("source").value === request.source &&
      $("title").value === request.title
    ) {
      $("source").value = "";
      $("title").value = "";
      $("generation-prompt").value = "";
      await saveDraft();
      updateCounts();
    }
    submissionId = crypto.randomUUID().replaceAll("-", "");
    await refresh();
    location.hash = "library";
    toast("Narration saved to your queue. You can close this page.");
    await showDetails(saved.id);
  });
};
$("prompt-form").onsubmit = (event) => {
  event.preventDefault();
  action(event.submitter, async () => {
    const b = brief();
    const result = await api("prompt", { brief: b });
    $("prompt-output").value = result.prompt;
    promptGeneratedFor = b;
    promptVersion = 2;
    updatePromptState();
    scheduleDraft();
    toast("Writing prompt ready.");
  });
};
bind("copy-prompt", async () => {
  if (!promptFresh()) return;
  try {
    await navigator.clipboard.writeText($("prompt-output").value);
    toast("Prompt copied. Paste it into your writing assistant.");
  } catch {
    $("prompt-output").focus();
    $("prompt-output").select();
    toast("Select Copy to copy your writing prompt.");
  }
});
bind("use-prompt", async () => {
  if (!promptFresh()) return;
  $("generation-prompt").value = $("prompt-output").value;
  if (!$("title").value) $("title").value = $("prompt-topic").value;
  scheduleDraft();
  location.hash = "new";
  $("source").focus();
  toast("Prompt saved with your draft. Paste the finished narration here.");
});
bind("pause-queue", async () => {
  await api("queue", { paused: !state.paused });
  await refresh();
  toast(
    state.paused
      ? "Future jobs paused. Active generation can finish."
      : "Queue resumed.",
  );
});
$("library-search").oninput = renderJobs;
document.querySelectorAll("[data-filter]").forEach(
  (b) =>
    (b.onclick = () => {
      filter = b.dataset.filter;
      document.querySelectorAll("[data-filter]").forEach((v) => {
        v.classList.toggle("active", v === b);
        v.setAttribute("aria-pressed", String(v === b));
      });
      renderJobs();
    }),
);
$("destination-form").onsubmit = (event) => {
  event.preventDefault();
  action(event.submitter, async () => {
    await api("destination", {
      destination: {
        tenantId: $("tenant-id").value.trim(),
        clientId: $("client-id").value.trim(),
        driveId: $("drive-id").value.trim(),
        folderId: $("folder-id").value.trim(),
      },
      autoPublish: $("auto-publish").checked,
      secret: $("client-secret").value || null,
    });
    $("client-secret").value = "";
    await refresh();
    toast("Destination saved. Check the folder to verify access.");
  });
};
bind("check-destination", async () => {
  await api("destination/check", {});
  await refresh();
  toast("Destination and read-only Anyone link verified.");
});
bind("share-destination", async () => {
  await api("destination/share", {});
  await refresh();
  toast("Read-only Anyone folder link is ready.");
});
bind("check-provider", async () => {
  $("provider-status").textContent = "Checking connection…";
  await api("providers/check", {
    engine: $("settings-provider").value,
    model: $("settings-model").value || null,
    apiKey: $("provider-key").value || null,
  });
  $("provider-key").value = "";
  await refresh();
  settingsProvider();
  refreshComposeVoices($("voice").value, $("model").value);
  toast("Speech connection verified.");
});
$("close-dialog").onclick = () => {
  $("detail-dialog").close();
  selectedId = null;
};
$("detail-dialog").addEventListener("close", () => (selectedId = null));
document.addEventListener("keydown", (event) => {
  if (
    event.key === "n" &&
    !["INPUT", "TEXTAREA", "SELECT"].includes(document.activeElement.tagName) &&
    !$("detail-dialog").open
  )
    location.hash = "new";
  if (
    (event.ctrlKey || event.metaKey) &&
    event.key === "Enter" &&
    !$("page-new").hidden
  ) {
    event.preventDefault();
    $("compose-form").requestSubmit($("create-narration"));
  }
});
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refresh();
  else if (ready) {
    clearTimeout(draftTimer);
    saveDraft();
  }
});
window.addEventListener("beforeunload", (event) => {
  if ($("draft-status").textContent !== "Draft saved" && ready) {
    event.preventDefault();
    event.returnValue = "";
  }
});
(async () => {
  await refresh();
  if (!state) return;
  options($("engine"), state.providers, state.defaults.engine);
  options($("settings-provider"), state.providers, state.defaults.engine);
  applyOptions(state.defaults);
  settingsProvider();
  const d = state.publishing.destination;
  $("tenant-id").value = d.tenantId;
  $("client-id").value = d.clientId;
  $("drive-id").value = d.driveId;
  $("folder-id").value = d.folderId;
  $("auto-publish").checked = state.publishing.autoPublish;
  try {
    const saved = await api("draft");
    draftRevision = saved.revision;
    loadDraft(saved.draft);
    $("draft-status").textContent = "Draft saved";
    ready = true;
  } catch (error) {
    message(error.message);
  }
  setInterval(() => {
    if (!document.hidden) refresh();
  }, 3000);
})();
