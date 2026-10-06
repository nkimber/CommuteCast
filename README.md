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

Queue files carry an application identity, schema version and migration history. Before migrating a legacy queue, the app creates a verified SQLite online backup under private `schema-backups`; it refuses foreign, newer or corrupt records without resetting them. These snapshots include committed WAL data. Local migration backups and recovered unreadable-draft copies remain separately retained when narrations are deleted. Complete current-state backup and restore are available through the packaged maintenance tool below; explicit command-line installation/update/rollback is described below; corporate deployment and fresh-machine acceptance remain release work.

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
- `tools/CommuteCast.Maintenance`: offline verified current-state backup, restore and interruption recovery.

See [implementation decisions](documents/Implementation-Decisions.md), [acceptance record](documents/Acceptance.md), [full verification matrix](documents/Verification-Matrix.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Recovery and privacy

Speech is restricted to `127.0.0.1:8765` / `8766`. Before sending text or starting a stopped container, the app verifies the local Docker context, CommuteCast/Compose labels, exact locally approved image ID, and loopback port binding. It waits at most two minutes for readiness, starts an owned container once, and never restarts a loading or active model. A failed model requires explicit repair and retry. Reprovisioning images is an explicit developer/setup action, never an application recovery action.

An exhausted recovery allowance is persisted per engine, shared by subsequent jobs and relaunches. Check speech readiness, audition, or explicitly retry a job after repair to allow a fresh bounded attempt. Only the exact local Docker Desktop Linux named pipe is accepted. A successful readiness check clears the outage record; no automatic container restart is performed.

Provider checks also verify the approved command/engine, no host mounts, bridge networking, resource limits and privilege configuration. A stopped container is reinspected after start. HTTP health bodies are limited to 64 KiB and audio to 64 MiB. Transient disconnect/429/500/502/503/504 failures permit at most two retries with capped backoff under one five-minute synthesis budget; incompatible settings, redirects, malformed audio, and other permanent errors require repair. Attempt files never replace prior output until complete lossless PCM validation and cancellation checks pass.

Chunk receipts contain checksums and a source/script/settings/provider fingerprint. A partial chunk has no receipt and is regenerated. Assembly includes all validated chunks once, normalizes to mono 24kHz 16-bit PCM, and encodes once at 128kbps. Short sentence/paragraph pauses are inserted at eligible chunk joins. The final MP3 is fully decoded, checked against expected duration, and checksummed before export. Destination copy and checksum verification precede a non-overwriting rename. The export journal reconciles a crash after rename without producing a second file.

Deletion stops the selected worker before removing tracked local artifacts. Export deletion is optional and refuses changed files. Delete all acts on recorded jobs, not an output-folder sweep. Cloud recycle bins, tenant retention, and phone downloads remain outside this app's control. Windows-user access control protects local storage; source/audio are not additionally encrypted by the app.

## Packaging and acceptance

`.\scripts\Publish-Portable.ps1` creates an unpackaged, self-contained Windows x64 folder and ZIP under `artifacts/release`. Docker, models, and FFmpeg are separate prerequisites. The app does not install them or elevate privileges. Code signing, a corporate installer, fresh-machine acceptance, listening approval, and actual corporate Android playback with the laptop off are release gates recorded separately. No evidence from a build or unit suite substitutes for those checks.

Publishing also writes `release-manifest.json`: a sorted file inventory with sizes/SHA256, app and binary build versions, bundled runtime, target architecture, supported schema range and provider contract. From the package's `app` directory, run `.\CommuteCast.Maintenance.exe verify-package --package '..'` before a deliberate update. Missing/changed/unlisted files, incompatible declarations and known private-state artifacts are refused. `seal-package` is a build action that regenerates the inventory; do not use it to approve an altered downloaded package. Checksums establish integrity against the supplied manifest; they do not establish trusted authorship or corporate signing approval. Explicit installation, rollback and uninstall commands are described below.

Close the editor and open `app\CommuteCast.Setup.exe` from the complete extracted portable package, or run `CommuteCast.Desktop.exe --setup`. Native setup reviews package identity/version, the separate binary/private folders, narration and managed-file counts, current release, rollback snapshot and pending recovery. Checking prerequisites is read-only. Install/update, rollback, recovery and uninstall require a separate review confirmation; uninstall keeps local data by default. A changed package, installation record, recovery journal or managed private inventory requires a fresh review. A locked, journal-bearing or incompatible queue has an explicitly unavailable count; it is never silently reset. Launch selects and verifies the installed release after setup releases its mutex.

Run uninstall from an external portable setup package, since an installed setup executable cannot remove its own running files. `--install-root` selects a dedicated local binary folder; `--root` is an explicit isolated private-workspace testing override and cannot rebind an owned installation. Native setup does not install/start prerequisites, grant corporate approval, or yet register Windows shortcuts/uninstall entries. Visual/confirmed native action acceptance, fresh-user/machine deployment and signing remain release gates.

## Back up and restore local state

In Settings, choose **Open backup & restore**. The app stops its current work and playback, saves the draft/settings and retains the exclusive workspace lease. Create a verified backup, choose and verify an existing completed backup, or recover an interrupted restore. Restore shows the exact backup date, ID and narration/file counts and requires a separate confirmation. Returning to the editor reloads local state; saved queue pause settings control dispatch. To enter maintenance when the queue or settings cannot load, close CommuteCast and launch `CommuteCast.Desktop.exe --maintenance` from the current release. Installed maintenance still verifies the active binary package and workspace binding; it never starts speech services.

For command-line maintenance, close CommuteCast first. From the portable package's `app` directory:

```powershell
.\CommuteCast.Maintenance.exe backup
# Use the completed folder reported by backup; retain it in private nonsynced storage.
.\CommuteCast.Maintenance.exe validate-backup --backup '<completed backup folder>'
.\CommuteCast.Maintenance.exe restore --backup '<completed backup folder>' --confirm-replace-local-data
.\CommuteCast.Maintenance.exe recover
```

Backups stay under `%LOCALAPPDATA%\CommuteCast\backups`. They contain private source, draft, queue/history, settings, locally approved provider identity, recovery allowance and current job audio/artifacts. A manifest records app/schema versions, file lengths and SHA256 checksums. The queue is captured through SQLite's online backup API; copying `queue.db` alone may miss committed WAL data. Backups exclude historical backup/recovery copies, audition audio, diagnostic exports, provisioning models and Docker images. Exported MP3s are separate files; maintenance never changes them. A backup does not install prerequisites or guarantee the approved provider image is available on another machine.

Restore validates the entire backup and stages another verified copy before replacing local managed state. Previous files remain under `recovery\restores\<restore ID>\previous`. An unfinished restore is recovered by desktop startup or `recover`; before its durable commit, recovery returns to the verified original state. Incompatible, changed or unsafe artifacts are refused and retained for inspection. Recovery copies and backups can contain private data and consume disk space; narration deletion and cache cleanup do not remove them. Keep the previous state until the restored application has been checked. Failed preparation can also leave retained copies for inspection.

For a reproducible isolated command-line acceptance run, use `.\scripts\Test-Maintenance.ps1` after publishing, or supply `-Executable` with the built maintenance executable. Its synthetic fixtures and report stay under ignored `artifacts\maintenance-acceptance`; it never restores the live user workspace. Native backup creation, restore confirmation/cancellation, editor transitions and lease exclusion have isolated observed evidence. Confirmed replacement and interruption recovery are covered by backend tests; a confirmed native replacement, full accessibility and fresh-machine acceptance remain open.

## Install, update, roll back and uninstall

Settings includes **Check setup**, a read-only inspection that works even when speech is unavailable. The maintenance tool exposes the same result with `check-setup [--root <private workspace>]`; install also reports it before activation. Checks cover Windows x64/build, running runtime, logical processors/physical RAM, supported user/system Docker installation locations and process presence, encoder/probe identity/build, WSL version, CLI, exact local Linux context, bounded daemon access, and verified owned-service health/selected voice. Foreign contexts never reach daemon/service inspection. Each command/service check has a five-second allowance and the entire report has a 30-second allowance. It does not launch Docker, start containers, reset recovery allowance, request speech, install/elevate, or write private state. `scripts/Test-Setup.ps1` verifies absent-folder preservation, private-file checksums and report redaction through the real executable.

The declared release baseline is Windows 11 x64 23H2 or newer, with the actual edition/servicing and corporate entitlement confirmed externally. WSL 2.1.5 is the minimum when using the WSL backend. BIOS virtualization, backend feature selection, corporate policy, endpoint restrictions, phone access and subjective/performance acceptance require separate evidence. Local checks deliberately report corporate approval as `ReviewRequired`; installed tools or a healthy API do not establish it. Docker binary version resources can be unavailable and are reported honestly. Readiness can locate both supported per-user and system Docker installations; setup inspection itself never launches them.

Close CommuteCast. Run these commands from an extracted portable package outside the installation folder:

```powershell
.\CommuteCast.Maintenance.exe install --package '<complete extracted portable folder>'
.\CommuteCast.Maintenance.exe inspect-install
.\CommuteCast.Maintenance.exe launch-installed
# Restore the recorded previous release AND its pre-update local state.
.\CommuteCast.Maintenance.exe rollback --confirm-replace-local-data
.\CommuteCast.Maintenance.exe recover-install
# Retain private data (default).
.\CommuteCast.Maintenance.exe uninstall --confirm-uninstall
# Deliberately remove recorded private source, queue, settings, audio and recovery copies.
.\CommuteCast.Maintenance.exe uninstall --confirm-uninstall --local-data remove --confirm-remove-local-data
```

Binaries stay in immutable verified `releases\<package identity>` folders under `%LOCALAPPDATA%\Programs\CommuteCast`; private data stays in `%LOCALAPPDATA%\CommuteCast`. Installation records bind those separate roots. `--install-root` selects a dedicated local binary folder; `--root` is an explicit isolated-workspace testing override. Installed startup verifies the active release and its bound workspace, including case-insensitive Windows paths. Launching an archived release or one with pending deployment is refused. Run `launch-installed` from the external portable maintenance tool to select the current release, or choose launch in native setup. Windows registration and automatic prerequisite installation remain separate work.

An update verifies the complete package, snapshots current private state, stages and verifies the binaries, checks/migrates the recognized queue schema, then atomically commits the active release. Rollback verifies the previous binary and pinned snapshot before restoring both; work created after that snapshot moves to retained recovery storage. A recovery snapshot permits undoing the rollback through the same command. Docker images/models are retained separately, so compatible provider availability must also be checked. Recovery follows a bounded journal: before activation it restores the original private state; after activation it retains the committed release. Unexpected or changed deployment records are preserved for inspection.

Uninstall defaults to retaining private data. Explicit removal covers recorded queue/settings/draft/provider recovery, job files and their backup/recovery folders. Exports, provisioning models, Docker artifacts and unknown root files remain separate. Removal locks and checks each file against its recorded checksum, deletes through that same Windows handle, and removes only empty directories. After uninstall commits, interrupted recorded removal resumes through `recover-install`; changed or unrecorded files stop removal for inspection. The small ownership/state/lease records remain to support safe recovery and reinstallation.

`.\scripts\Test-Installation.ps1` runs real packaged commands against separate synthetic fixtures: update, rollback/undo, immutable queued data and PCM checksums, exclusion and both uninstall scopes. `-KeepInstalled` leaves a paused fixture for native checks. Fixtures stay under ignored `artifacts\installation-acceptance`. Signing, corporate deployment policy, complete prerequisite acceptance and fresh-user/machine acceptance remain release work.
