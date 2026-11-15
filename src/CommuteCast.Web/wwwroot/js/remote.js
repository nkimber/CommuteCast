const $ = (id) => document.getElementById(id);
const csrf = document.querySelector('meta[name="csrf-token"]').content;
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: "short", timeStyle: "short" });
let workers = [];
let presets = [];
let loading = false;
let sending = false;
let available = false;
let pending;

async function api(endpoint, body) {
    const response = await fetch(`/api/remote${endpoint}`, body === undefined ? { cache: "no-store" } : {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf },
        body: JSON.stringify(body)
    });
    if (response.status === 401 || response.status === 403) throw new Error("Sign in again to access the remote queue.");
    const result = await response.json();
    if (!response.ok) throw new Error(result.error || "The shared queue is temporarily unavailable. Try again.");
    return result;
}

function when(utc) {
    const date = new Date(utc);
    return Number.isNaN(date.getTime()) ? "" : dateFormat.format(date);
}

function voiceText(voice) {
    if (!voice) return "";
    return [voice.engine, voice.model, voice.voice, voice.preset, `${voice.speed.toFixed(2)}× pace`, voice.emotion,
        voice.localDelivery?.blendVoice ? `Blend ${voice.localDelivery.blendVoice}` : "",
        voice.hasDeliveryInstructions ? "Custom delivery instructions" : ""].filter(Boolean).join(" · ");
}

function statusLabel(state) {
    return state === "NeedsAttention" ? "Needs attention" : state;
}

function updateSelect(select, items, getKey, getLabel, fallbackKey, permitted = () => true) {
    const prior = select.value;
    const options = [new Option("Choose…", "")];
    for (const item of items) {
        const option = new Option(getLabel(item), getKey(item));
        option.disabled = !permitted(item);
        options.push(option);
    }
    select.replaceChildren(...options);
    if (items.some((item) => getKey(item) === prior)) select.value = prior;
    else if (!prior && fallbackKey) select.value = fallbackKey;
}

function updateDetails() {
    const worker = workers.find((item) => item.workerId === $("worker").value);
    const preset = presets.find((item) => item.id === $("preset").value);
    $("worker-detail").textContent = worker ? [
        `Last check-in ${when(worker.updatedUtc)}`,
        worker.state === "Busy" ? "Your narration will wait in the queue." : "",
        worker.state === "Offline" || worker.state === "Stopped" ? "The worker can pick it up when it returns." : "",
        worker.presets.length ? `Verified choices: ${worker.presets.join(", ")}` : ""
    ].filter(Boolean).join(" · ") : "";
    $("voice-detail").textContent = voiceText(preset?.voice);
    $("send").disabled = !available || sending || !worker || worker.state === "Disabled" || !preset;
}

function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text) node.textContent = text;
    if (className) node.className = className;
    return node;
}

function renderJob(job) {
    const status = job.status;
    const label = status?.state === "Ready" && status.fallbackReason ? "Ready · fallback used" :
        statusLabel(status?.state || "Waiting for pickup");
    const row = element("li", "", "episode-row remote-job");
    row.dataset.id = job.id;
    row.append(element("strong", job.title), element("small", [label, job.worker, when(job.createdUtc)].filter(Boolean).join(" · ")));
    if (!status) return row;
    if (status.state === "Queued" && status.queuePosition) row.append(element("p", `Queue position ${status.queuePosition}`));
    if (status.state === "Generating" && status.chunksTotal) {
        const estimate = status.estimatedSecondsRemaining > 0 ? ` · about ${Math.ceil(status.estimatedSecondsRemaining / 60)} min` : "";
        row.append(element("p", `${status.chunksDone} of ${status.chunksTotal} chunks${estimate}`));
    }
    if (status.fallbackReason) row.append(element("p", status.fallbackReason, "fallback"));
    if (status.attention) row.append(element("p", status.attention, "fallback"));
    const details = element("details");
    details.append(element("summary", "Voice and delivery details"), element("p", `Requested: ${voiceText(job.requestedVoice)}`));
    if (status.actualVoice) details.append(element("p", `Used: ${voiceText(status.actualVoice)}`));
    if (status.state === "Ready" && status.outputFile) details.append(element("p", status.outputFile));
    details.append(element("small", `Updated ${when(status.updatedUtc)}`));
    row.append(details);
    return row;
}

async function refresh(force = false) {
    if (loading || sending || (!force && document.hidden)) return;
    loading = true;
    try {
        const state = await api("");
        available = state.configured;
        $("request-form").hidden = !available;
        if (!available) {
            $("connection").textContent = "Set the shared queue folder on the web host before sending a narration.";
            return;
        }
        workers = state.workers;
        presets = state.presets;
        const preferred = workers.find((worker) => worker.state === "Ready") ||
            workers.find((worker) => worker.state === "Busy") || workers.find((worker) => worker.state !== "Disabled");
        if (document.activeElement !== $("worker") || $("worker").options.length === 0) {
            updateSelect($("worker"), workers, (worker) => worker.workerId, (worker) => `${worker.name} · ${statusLabel(worker.state)}`,
                preferred?.workerId, (worker) => worker.state !== "Disabled");
        }
        if (document.activeElement !== $("preset") || $("preset").options.length === 0) {
            updateSelect($("preset"), presets, (preset) => preset.id, (preset) => preset.name, presets[0]?.id);
        }
        $("connection").textContent = workers.some((worker) => worker.state === "Ready" || worker.state === "Busy") ? "" :
            preferred ? "No worker is ready now. You can still queue a narration for later." : "Register a worker from the Windows app.";
        $("folder").hidden = !state.listeningFolderUrl;
        if (state.listeningFolderUrl) $("folder").href = state.listeningFolderUrl;
        const expanded = new Set(Array.from($("jobs").querySelectorAll("details[open]"), (detail) => detail.parentNode.dataset.id));
        const rows = state.jobs.map(renderJob);
        for (const row of rows) {
            if (expanded.has(row.dataset.id)) row.querySelector("details")?.setAttribute("open", "");
        }
        $("jobs").replaceChildren(...rows);
        $("jobs-empty").textContent = rows.length ? "" : "No remote narrations yet.";
    } catch (error) {
        available = false;
        $("connection").textContent = error.message;
    } finally {
        loading = false;
        updateDetails();
    }
}

$("request-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    if (sending || $("send").disabled) return;
    const form = { targetWorkerId: $("worker").value, presetId: $("preset").value, title: $("title").value, text: $("text").value };
    const key = JSON.stringify(form);
    if (!pending || pending.key !== key) pending = { key, body: { ...form, jobId: crypto.randomUUID().replaceAll("-", ""), createdUtc: new Date().toISOString() } };
    sending = true;
    updateDetails();
    try {
        await api("/jobs", pending.body);
        pending = undefined;
        if ($("title").value === form.title) $("title").value = "";
        if ($("text").value === form.text) $("text").value = "";
        $("result").textContent = "Request saved. Check Remote narrations for worker pickup and progress.";
    } catch (error) {
        $("result").textContent = error.message;
    } finally {
        sending = false;
        updateDetails();
        void refresh(true);
    }
});

$("worker").addEventListener("change", updateDetails);
$("preset").addEventListener("change", updateDetails);
$("refresh").addEventListener("click", () => void refresh(true));
document.addEventListener("visibilitychange", () => void refresh());
setInterval(refresh, 15_000);
void refresh(true);
