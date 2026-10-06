# CommuteCast

A native Windows WPF utility that turns pasted text into a single MP3 using local Docker speech. Source, preparation, queue, chunks, and diagnostics stay under `%LOCALAPPDATA%\CommuteCast`. Only a completed, validated MP3 is published to your chosen local folder. OneDrive handles synchronization separately; the application reports **cloud upload unknown**.

## Run on this machine

Prerequisites: Windows, .NET 10 SDK (or the portable self-contained package), approved Docker Desktop with its local Linux-container context, and FFmpeg / FFprobe on PATH. These tools are already present on the development machine. No administrator privileges are needed by the app.

```powershell
# One-time speech provisioning. Downloads public packages and model files during build.
.\scripts\Provision-Speech.ps1 -Build

# Build and launch the desktop application.
.\scripts\Start-CommuteCast.ps1
```

On first use, choose your actual corporate OneDrive folder, audition Kokoro and Piper, and select a voice and pace. Paste text, edit the suggested title, inspect **Review spoken text**, and click **Queue narration** (Ctrl+Enter). The library displays generation, errors, local playback, cancellation, and retry. Keep the app open and the laptop awake while generating. Closing saves the queue; cancelled jobs require explicit retry. Pause stops future dispatch, allowing the current job to finish.

Pending narrations can be moved earlier/later in the library; their order survives relaunch. Settings provides storage usage, intermediate-cache quota (default 1024 MiB), scratch retention (7 days), and private storage budget (10240 MiB). Eligible cache is cleaned after each job and before submission; manual cleanup is also available. Cleanup preserves sources/history, finished MP3s, active artifacts, and chunks needed for retry. New submissions are refused if their conservative storage estimate exceeds the budget or free-space floor. Delete older narrations explicitly to remove protected data.

Storage admission reserves estimated audio for all pending jobs, subtracts their existing private files, and applies on retry as well as submission. If a durable checkpoint write fails, dispatch pauses with a repair instruction; existing records are retained. Repair storage, retry the affected narration, then resume the queue. A failed retry does not change the saved stage or destination.

The input limit is 250,000 characters, with no editor truncation. Default preparation removes common Markdown formatting while preserving content. Links retain labels and URLs; tables retain cells; fenced code is spoken unless explicitly excluded. Numbers are preserved for the selected engine to pronounce. Pronunciation substitutions use literal, whole-term `term=spoken words` rules and are visible in the preparation map. Oversized sentences use a deterministic word/Unicode-safe fallback. Automatic checks establish source/chunk accounting and audio integrity, **not exact spoken fidelity**.

## Validation

```powershell
dotnet build CommuteCast.slnx
dotnet test CommuteCast.slnx
# Real Docker speech → normalized PCM → one MP3 → verified local export.
dotnet run --project tools\CommuteCast.Pilot -- kokoro
dotnet run --project tools\CommuteCast.Pilot -- piper
```

Tests cover source span accounting, chunk ordering, Unicode boundaries, frozen settings, SQLite relaunch, cancellation, export collisions, rename reconciliation, changed-file deletion guards, cache corruption, and export-only retry. Pipeline tests require FFmpeg and FFprobe. Pilot output and reports are private, ignored files in `artifacts/pilot`; they do not upload to OneDrive.

## Architecture

- `src/CommuteCast.Core`: domain records, deterministic preparation, coverage and chunking; no WPF or provider JSON dependency.
- `src/CommuteCast.Infrastructure`: SQLite with FULL synchronous WAL writes, bounded process execution, verified loopback provider, PCM/MP3 validation, recoverable export, serial queue.
- `src/CommuteCast.Desktop`: WPF/MVVM capture, settings, library, preparation review, auditions, local playback, deletion, diagnostics.
- `services/speech`: pinned CPU Kokoro ONNX and Piper packages/models; a versioned CommuteCast HTTP contract; no runtime downloads or content logging.
- `tools/CommuteCast.Pilot`: reproducible real-service acceptance runner.

See [implementation decisions](documents/Implementation-Decisions.md), [acceptance record](documents/Acceptance.md), [full verification matrix](documents/Verification-Matrix.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Recovery and privacy

Speech is restricted to `127.0.0.1:8765` / `8766`. Before sending text or starting a stopped container, the app verifies the local Docker context, CommuteCast/Compose labels, exact locally approved image ID, and loopback port binding. It waits at most two minutes for readiness, starts an owned container once, and never restarts a loading or active model. A failed model requires explicit repair and retry. Reprovisioning images is an explicit developer/setup action, never an application recovery action.

An exhausted recovery allowance is persisted per engine, shared by subsequent jobs and relaunches. Check speech readiness, audition, or explicitly retry a job after repair to allow a fresh bounded attempt. Only the exact local Docker Desktop Linux named pipe is accepted. A successful readiness check clears the outage record; no automatic container restart is performed.

Provider checks also verify the approved command/engine, no host mounts, bridge networking, resource limits and privilege configuration. A stopped container is reinspected after start. HTTP health bodies are limited to 64 KiB and audio to 64 MiB. Transient disconnect/429/500/502/503/504 failures permit at most two retries with capped backoff under one five-minute synthesis budget; incompatible settings, redirects, malformed audio, and other permanent errors require repair. Attempt files never replace prior output until complete lossless PCM validation and cancellation checks pass.

Chunk receipts contain checksums and a source/script/settings/provider fingerprint. A partial chunk has no receipt and is regenerated. Assembly includes all validated chunks once, normalizes to mono 24kHz 16-bit PCM, and encodes once at 128kbps. Short sentence/paragraph pauses are inserted at eligible chunk joins. The final MP3 is fully decoded, checked against expected duration, and checksummed before export. Destination copy and checksum verification precede a non-overwriting rename. The export journal reconciles a crash after rename without producing a second file.

Deletion stops the selected worker before removing tracked local artifacts. Export deletion is optional and refuses changed files. Delete all acts on recorded jobs, not an output-folder sweep. Cloud recycle bins, tenant retention, and phone downloads remain outside this app's control. Windows-user access control protects local storage; source/audio are not additionally encrypted by the app.

## Packaging and acceptance

`.\scripts\Publish-Portable.ps1` creates an unpackaged, self-contained Windows x64 folder and ZIP under `artifacts/release`. Docker, models, and FFmpeg are separate prerequisites. The app does not install them or elevate privileges. Code signing, a corporate installer, fresh-machine acceptance, listening approval, and actual corporate Android playback with the laptop off are release gates recorded separately. No evidence from a build or unit suite substitutes for those checks.
