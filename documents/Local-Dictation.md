# Local listening-goal dictation

In **Build a prompt**, use **Record** beside **What should the listener take away?**, speak in English, then choose **Stop and transcribe**. The transcript is appended to the current textbox, which remains editable. Building the prompt includes the recorded goal; changing the goal marks any previously generated prompt as stale. The normal draft persistence saves the resulting text.

## Setup

Docker Desktop must be running with its local Linux context selected. From the CommuteCast folder:

```powershell
.\scripts\Provision-Dictation.ps1 -Build
```

This builds `commutecast-whisper:1`, downloads the pinned `Systran/faster-whisper-small.en` model revision `d1d751a5f8271d482d14ca55d9e2deeebbae577f`, and provisions only `commutecast-whisper`. The model lives in the image and survives container restarts. The first build needs internet access and space for the model and Python runtime. The existing narration services are independent. Reprovision only when dictation is idle.

Subsequent setup can omit `-Build`. In the app, **Check / repair dictation** validates ownership, the installed image identity and the localhost binding, starts a stopped container and waits for model readiness. It does not build missing images or replace an incompatible container. Errors include setup instructions. Docker's restart policy restarts the provisioned service after Docker restarts.

Enable Windows **Settings > Privacy & security > Microphone > Let desktop apps access your microphone**. The app records the Windows default input device using NAudio as mono 16 kHz PCM16. Choose your preferred device in Windows sound settings. Recording starts only after Whisper is ready.

## Behavior and limits

- Recordings stop after two minutes and transcribe automatically. CPU inference uses INT8, four threads, and a three-GiB container limit. English is the configured language.
- **Cancel** discards the active recording/request without changing the goal. Navigating away, suspending or closing also cancels. A late response from a cancelled request cannot modify the goal. Server inference already started may finish before another request is admitted.
- Existing text and edits made during recording are retained. If the complete transcript would exceed the 2,000-character goal limit, it remains visible for copying or adding after shortening the goal. This pending overflow transcript is held in memory; add or copy it before closing. No text is silently truncated.
- Silence produces a retry message. A missing/disabled microphone, stopped Docker, busy inference or failed transcription preserves the existing goal.
- Microphone audio stays in bounded process memory and is sent only to `127.0.0.1:8767`. The service does not persist audio or transcripts or log request bodies. Model inference runs offline. The resulting goal is saved locally by the normal draft workflow and is included in the prompt you copy to your chosen GAI tool.

## Verification

`scripts/Test-Dictation.ps1` synthesizes a known sentence using Windows speech, transcribes it through the live service, checks its key phrases and records measured latency under ignored `artifacts/dictation-acceptance`:

```powershell
powershell.exe -NoProfile -File .\scripts\Test-Dictation.ps1
$env:COMMUTECAST_TEST_DICTATION = '1'
$env:COMMUTECAST_DICTATION_SAMPLE = "$PWD\artifacts\dictation-acceptance\known-goal.wav"
dotnet test .\tests\CommuteCast.Desktop.Tests -c Release --filter FullyQualifiedName~LocalWhisperNativeBindingAcceptanceWhenRequested
```

The opt-in native test passes that audio through the real Whisper client and WPF commands/textbox, checks append/stale state and disk persistence, and renders the prompt builder. It substitutes a file for microphone capture so the known speech is deterministic. Set `COMMUTECAST_TEST_MICROPHONE=1` and run `PhysicalMicrophoneCaptureWhenRequested` separately to check the actual Windows default microphone and WAV upload; it briefly records ambient audio in memory without saving it.

Automated desktop regressions use fake microphone/service boundaries to cover cancellation, late results, errors, silence, overflow, lifecycle cleanup and the duration limit. Service tests reject malformed, truncated, wrong-format and overlong audio and verify inference-gate cleanup. These checks do not establish transcription accuracy for an individual's accent or real room conditions; review your first spoken goal in the textbox.

## Verified checkpoint: October 11, 2026

- The Docker image built with locked Python dependencies and the immutable model revision. `commutecast-whisper` is healthy on localhost; its running image identity matches the installed tag, and its `/app/app.py` SHA256 matches the checked-in source. Docker's built-in bridge avoids the exhausted custom-network address pool on this machine.
- The known sentence transcribed exactly: “The listener should understand how railways changed cities and learn why public transport matters.” The initial CPU request took 17.65 seconds under concurrent local build/container load; this is a fixture measurement, not a general latency guarantee.
- All 15 dictation desktop cases passed with both opt-in acceptances enabled. The live WPF case stopped Whisper first and verified that recording readiness started it again, transcribed the known file, appended to the native textbox, marked the old prompt stale, and saved the draft. The physical default microphone supplied PCM samples and its WAV was accepted by Whisper. The first 800-millisecond hardware fixture captured no samples; the corrected fixture waits up to five seconds for device startup. Recordings with insufficient audio now show a specific microphone/input message.
- All 37 pre-existing desktop cases passed in the earlier complete regression run. All five new container-policy cases and all seven offline Python service cases passed. The ten-project Release build passed with zero warnings/errors; the portable Windows publish and inventory seal passed after excluding local Python caches and `.dockerignore` from packaging. Audio samples, reports, screenshots and the relocated generated cache remain under ignored `artifacts`.
- Actual user speech/accents, real OS suspend, and interactive use of the deployed controls remain separate acceptance checks. The development app already running during implementation was preserved; close it and launch the updated Release build to use the new controls.
