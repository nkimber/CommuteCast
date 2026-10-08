# CommuteCast

A native Windows WPF utility that turns pasted text into a single MP3 using local Docker speech. Source, preparation, queue, chunks, and diagnostics stay under `%LOCALAPPDATA%\CommuteCast`. Only a completed, validated MP3 is published to your chosen local folder. OneDrive handles synchronization separately; the application reports **cloud upload unknown**.

## Build in Visual Studio

Open **CommuteCast.sln** in Visual Studio 2026 with the **.NET desktop development** workload and .NET 10 SDK. The checked-in `.vsconfig` identifies that workload. The solution contains all eight projects and lists **CommuteCast.Desktop** first. Select it as the startup project if Visual Studio has saved a different choice, build the solution, then press F5 to run the native WPF client. **CommuteCast.slnx** contains the same projects for tools that use the XML solution format. Microsoft documents this WPF/.NET 10 setup in its [Visual Studio tutorial](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/getting-started).

The client project sets `OutputType=WinExe`, `TargetFramework=net10.0-windows`, and `UseWPF=true`; its application and windows are compiled XAML with C# code-behind. A Release build produces `src\CommuteCast.Desktop\bin\Release\net10.0-windows\CommuteCast.Desktop.exe`. Docker, FFmpeg and speech provisioning are runtime prerequisites for narration; they are not required to compile or open the client.

```powershell
dotnet build .\CommuteCast.sln -c Release
```

If opening the solution appears to close Visual Studio, check whether an existing **CommuteCast** window is still open. A loaded solution shows **8 of 8 projects** in Solution Explorer. To capture a recurring failure, launch the IDE directly with [activity logging](https://learn.microsoft.com/en-us/visualstudio/ide/reference/log-devenv-exe?view=visualstudio) from the repository folder:

```powershell
$vsPath = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Workload.ManagedDesktop -property installationPath
if (-not $vsPath) { throw 'Visual Studio with the .NET desktop development workload was not found.' }
$log = Join-Path $env:TEMP ("CommuteCast-VS-" + [guid]::NewGuid().ToString('N') + '.xml')
& (Join-Path $vsPath 'Common7\IDE\devenv.exe') (Join-Path $PWD 'CommuteCast.sln') /Log $log
```

The log can contain local paths and IDE details; inspect it locally. If the failure repeats, launch with `/SafeMode /Log $log` to compare with the [default IDE environment and services](https://learn.microsoft.com/en-us/visualstudio/ide/reference/safemode-devenv-exe?view=visualstudio). Preserve the failing log before retrying. A successful SDK or Visual Studio command-line build establishes compilation, while interactive loading and F5 need separate observation.

GitHub's **Windows build and regression** workflow builds the native solution, runs the complete synthetic/audio regression, publishes a self-contained Windows x64 ZIP and verifies a fresh extraction. It pins .NET SDK 10.0.401 (with patch roll-forward), action commit revisions and the SHA256 of the [FFmpeg 8.0.1 test build](https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1). Test reports and successful ZIPs are retained as seven-day Actions artifacts. A passing workflow establishes build/regression/package integrity; actual Docker, native UI, voice judgment, signing and corporate/phone acceptance remain separate.

## Run on this machine

Operational debugging logs are written automatically under `%LOCALAPPDATA%\CommuteCast\logs`. See [Logging and debugging](documents/Logging.md) for reading errors by narration ID, enabling Debug detail and checking speech container logs.

Prerequisites: Windows, .NET 10 SDK (or the portable self-contained package), approved Docker Desktop with its local Linux-container context, and FFmpeg / FFprobe on PATH. FFmpeg must advertise `fd` under `Output:` in `ffmpeg -hide_banner -protocols`; the read-only setup check reports this capability. These tools are already present on the development machine. No administrator privileges are needed by the app.

```powershell
# One-time speech provisioning. Downloads public packages and model files during build.
.\scripts\Provision-Speech.ps1 -Build

# Build and launch the desktop application.
.\scripts\Start-CommuteCast.ps1
```

On first use, choose your actual corporate OneDrive folder, audition Kokoro and Piper, and select a voice and pace. Paste text, edit the suggested title, inspect **Review spoken text**, and click **Queue narration** (Ctrl+Enter). The library displays generation, errors, local playback, cancellation, and retry. Keep the app open and the laptop awake while generating. Closing saves the queue; cancelled jobs require explicit retry. Pause stops future dispatch, allowing the current job to finish.

A successful submission opens its saved item in **Your library**. Returning to **New narration** shows the latest saved item's stage and validated-chunk progress, with a link back to that exact item. Waiting, paused, active, failed and exported states have separate explanations; completing all chunks still leaves assembly, final validation and export. If a saved item needs attention, resolve its displayed error and use **Retry / resume** on that item rather than submitting another copy.

**Narration Details** supports selecting text and Ctrl+C. **Copy all details** copies the selected job's ID, submission time, settings/progress, delivery state, error, repair instructions and latest speech-check result, without the source or spoken script. Failed narrations show **How to fix** immediately after the error. **Check saved speech service** checks the engine captured by that narration, resets its recovery allowance explicitly and verifies compatibility with its captured model/image. The result remains visible and copyable for that job. An exhausted automatic allowance can remain after provisioning or restarting; use the explicit check, then **Retry / resume** on the saved item when ready.

**Repair speech & resume** combines that explicit check with retrying the same saved narration. It starts installed Docker Desktop and verified stopped CommuteCast containers when needed, checks the captured model/image, and preserves a paused queue. If paused, choose **Resume queue** afterward. Settings and the attention banner also offer **Start / repair speech services**, which checks both engines and refreshes setup without retrying failed narrations. Running containers are checked without restarting them; missing or unverified containers still require the approved provisioning script.

Speech-readiness failures, queue-storage failures and failed saved items appear in a prominent warning above every page. The speech warning remains visible across navigation and unrelated successful actions until readiness succeeds. **Check speech readiness** rechecks installed services; **Open setup checks** opens Settings. A message that speech services are not provisioned requires the one-time provisioning command above. Readiness does not perform that installation. A rejected submission raises an error and leaves the draft available; the static queue explanation does not confirm that a job was saved.

Use **Play standard sample**, or select a short passage in the editor and choose **Play selected text**. The sample uses the captured voice, pace and pronunciation options and waits for current narration. **Stop audition** stops playback and requests cancellation of a waiting/generating sample. Selections and prepared scripts must fit 900 characters; nothing is silently shortened. A cropped selection inside excluded code is refused. Audition audio stays private and does not create a library item or export. Local-provider previews persist only file ownership metadata in the schema-4 database, without selected text or job history. Stop and startup recovery remove verified original files; replacements, changed files and unrecorded outputs are preserved for inspection. Active or interrupted ownership records must be reconciled in their original workspace before backup or restore.

Speech cancellation retires a reservation tied to the exact running service process. The app waits up to eight seconds for active inference to finish; if it remains active or cannot be verified, a source-free `speech-admission.json` record blocks new speech on both engines until a later readiness check settles it. No container is restarted to clear an active reservation. This current-runtime record survives restoring narration history. Do not remove it to bypass settlement. Updated desktop builds require the reservation-capable speech image: run the provisioning build after updating the application. Existing narrations retain their original model/image fingerprints and require their original compatible service for regeneration.

If the owned container has stopped, readiness verifies its complete configuration and stable image pin before settling the old reservation and starting the owned service. A missing daemon, foreign/missing container, OOM or paused state keeps the fence and requires repair. Read-only inspection preserves the reservation and never starts a service. An active reservation timeout instructs you to wait and retry.

Pending narrations can be moved earlier/later in the library; their order survives relaunch. Settings provides storage usage, intermediate-cache quota (default 1024 MiB), scratch retention (7 days), and private storage budget (10240 MiB). Eligible cache is cleaned after each job and before submission; manual cleanup is also available. Cleanup preserves sources/history, finished MP3s, active artifacts, and chunks needed for retry. New submissions are refused if their conservative storage estimate exceeds the budget or free-space floor. Delete older narrations explicitly to remove protected data.

Storage admission reserves estimated audio for all pending jobs, subtracts their existing private files, and applies on retry as well as submission. If a durable checkpoint write fails, dispatch pauses with a repair instruction; existing records are retained. Repair storage, retry the affected narration, then resume the queue. A failed retry does not change the saved stage or destination.

Optional managed-export deletion validates and removes the finished MP3 through one Windows file handle. A changed file or an open writer causes an incomplete removal that can be retried after the conflict is resolved; unrelated output files are preserved. Removing a local export does not establish erasure of cloud retention or phone copies.

Interrupted publication resumes only a partial with the recorded Windows file identity and bytes matching the finished private MP3. Changed or replacement partial files are kept for inspection. Selecting a replacement output folder can leave unverified staging in the previous folder; the narration details retain a notice. Older incomplete staging without an identity and a file created before its ownership checkpoint require inspection; no existing collision is assumed disposable.

Queue files carry an application identity, schema version and migration history. Before migrating a legacy queue, the app creates a verified SQLite online backup under private `schema-backups`; it refuses foreign, newer or corrupt records without resetting them. These snapshots include committed WAL data. Local migration backups and recovered unreadable-draft copies remain separately retained when narrations are deleted. Complete current-state backup and restore are available through the packaged maintenance tool below; explicit command-line installation/update/rollback is described below; corporate deployment and fresh-machine acceptance remain release work.

The input limit is 250,000 characters, with no editor truncation. Default preparation removes common Markdown formatting while preserving content. Links retain labels and URLs; tables retain cells; fenced code is spoken unless explicitly excluded. Numbers and uppercase words stay as written by default, for the engine to pronounce. In Settings, choose English digit/symbol reading, integer/decimal values, scientific exponents, uppercase letter spelling, and explicit ISO, month/day/year or day/month/year calendar interpretation. Invalid chosen dates stay literal and produce a review warning. These preparation options work with both supported English speech contracts; an unknown language/profile/engine disables the controls and refuses new narration until a supported profile is selected.

Pronunciation substitutions use ordered literal, whole-term `term=spoken words` rules. Dictionary replacements take priority over number/date/uppercase options, including identity replacements; later dictionary entries can replace earlier results. The dictionary supports 256 entries, terms up to 128 characters, replacements up to 1,024 characters and 16,384 total characters. Preparation also bounds script and review expansion rather than truncating it. Review spoken text shows the exact final script, profile, dictionary content revision, and each change's original UTF-16 source offset/range, before/after text and rule. Synthetic table/code cues are protected from pronunciation rewriting. Audition uses the selected profile and dictionary. Every queued narration retains its immutable profile and prepared script; reuse creates a new draft. Legacy jobs retain their original preparation and cache fingerprint. Oversized sentences use a deterministic word/Unicode-safe fallback. Automatic checks establish source/chunk accounting and audio integrity, **not exact spoken fidelity**.

## Validation

```powershell
dotnet build CommuteCast.slnx
dotnet test CommuteCast.slnx
# Real Docker speech → normalized PCM → one MP3 → verified local export.
dotnet run --project tools\CommuteCast.Pilot -- kokoro
dotnet run --project tools\CommuteCast.Pilot -- piper
# Synthetic 12-text preparation/chunk corpus; requires no Docker service.
dotnet run --project tools\CommuteCast.Pilot -- --verify-corpus
# Complete 20–30-minute synthetic listening fixture; allow up to one hour.
dotnet run --project tools\CommuteCast.Pilot -- piper artifacts/pilot/piper-long-form-example --verify-long-form
# Active real inference cancellation, followed by the opposite engine.
dotnet run --project tools\CommuteCast.Pilot -- kokoro artifacts/pilot/kokoro-cancellation-example --verify-cancellation
dotnet run --project tools\CommuteCast.Pilot -- piper artifacts/pilot/piper-cancellation-example --verify-cancellation
# Isolated fault acceptance: close the desktop and other Pilot runs first.
# Terminates only the verified owned service during its synthetic request.
dotnet run --project tools\CommuteCast.Pilot -- piper artifacts/pilot/piper-active-loss-example --verify-active-service-loss
dotnet run --project tools\CommuteCast.Pilot -- kokoro artifacts/pilot/kokoro-active-loss-example --verify-active-service-loss
# Queue-host loss: terminates only its own child during second-chunk inference.
# Run one acceptance case at a time with the desktop and other Pilot clients closed.
dotnet run --project tools\CommuteCast.Pilot -- piper artifacts/pilot/piper-host-loss-example --verify-queue-host-loss
dotnet run --project tools\CommuteCast.Pilot -- kokoro artifacts/pilot/kokoro-host-loss-example --verify-queue-host-loss
# Synthetic completed private-audio rename boundaries: four owned child terminations.
pwsh -File scripts\Test-PrivatePromotionCrash.ps1
# Synthetic incomplete private writer/receipt boundaries: four owned child terminations.
pwsh -File scripts\Test-PrivateWriteCrash.ps1
# Actual adapter-host loss at five download checkpoints per synthetic contract.
# No real Docker daemon, HTTP server or speech model is contacted.
pwsh -File scripts\Test-SpeechWriteCrash.ps1
# Actual preview-host loss at five checkpoints per synthetic contract; source-free ledger recovery.
pwsh -File scripts\Test-AuditionWriteCrash.ps1
# Retained older binary against an isolated current-schema queue.
# Supply the maintenance executable from a previously extracted older-schema package.
pwsh -File scripts\Test-SchemaCompatibility.ps1 -OlderMaintenance C:\path\to\older\app\CommuteCast.Maintenance.exe
```

Tests cover source span accounting, chunk ordering, Unicode boundaries, frozen settings, SQLite relaunch, cancellation, export collisions, rename reconciliation, changed-file deletion guards, cache corruption, and export-only retry. Pipeline tests require FFmpeg and FFprobe. Pilot output and reports are private, ignored files in `artifacts/pilot`; they do not upload to OneDrive.

The real-service pilot uses a fresh isolated folder, the locally pinned image, and an English technical pronunciation profile (scientific numbers, uppercase letter spelling, ISO dates and dictionary overrides). It verifies raw/script accounting, complete ordered receipts, the durable frozen snapshot, full audio validation and byte-identical local export. Its report records readiness separately from generation/validation/export time. Current smoke tests pass for both engines; subjective voice quality, longer workload approval and phone/cloud acceptance remain open. On this machine, the committed-source confirmation generated 120 seconds of Kokoro audio in 177 seconds and 114 seconds of Piper audio in 39 seconds; these are single-workload observations, not accepted performance guarantees.

## Architecture

New submissions bind their model fingerprint to the current verified speech image. Metadata from an older image is refreshed for the engine selected when submission began. Compatible saved metadata supports queuing while speech is busy or unavailable; saved jobs retain their original configuration and receipts. If an image changes, unfinished generation requires the original compatible service or a new submission. Existing completed audio can still be exported without regenerating it.

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

Deletion stops selected active work before removing any selected item's local artifacts. Delete all records the reviewed items' removal requests together before starting removal; new submissions, retries and cleanup wait for that operation. Each failed item remains visible for retry, and recorded interrupted requests finish on relaunch. A checkpoint failure pauses dispatch until repaired. Export deletion is optional and refuses changed files; retrying a pending deletion completes its already recorded export scope. Delete all acts on reviewed job identities, never an output-folder sweep or a narration submitted afterward. The current editor draft, backups, unrelated files, models, cloud recycle bins, tenant retention and phone downloads remain outside this operation. Windows-user access control protects local storage; source/audio are not additionally encrypted by the app.

## Packaging and acceptance

`.\scripts\Publish-Portable.ps1` creates an unpackaged, self-contained Windows x64 folder and ZIP under `artifacts/release`. Docker, models, and FFmpeg are separate prerequisites. The app does not install them or elevate privileges. Code signing, a corporate installer, fresh-machine acceptance, listening approval, and actual corporate Android playback with the laptop off are release gates recorded separately. No evidence from a build or unit suite substitutes for those checks.

Publishing also writes `release-manifest.json`: a sorted file inventory with sizes/SHA256, app and binary build versions, bundled runtime, target architecture, supported schema range and provider contract. From the package's `app` directory, run `.\CommuteCast.Maintenance.exe verify-package --package '..'` before a deliberate update. Missing/changed/unlisted files, incompatible declarations and known private-state artifacts are refused. `seal-package` is a build action that regenerates the inventory; do not use it to approve an altered downloaded package. Checksums establish integrity against the supplied manifest; they do not establish trusted authorship or corporate signing approval. Explicit installation, rollback and uninstall commands are described below.

Close the editor and open `app\CommuteCast.Setup.exe` from the complete extracted portable package, or run `CommuteCast.Desktop.exe --setup`. Native setup reviews package identity/version, the separate binary/private folders, narration and managed-file counts, current release, rollback snapshot and pending recovery. Checking prerequisites is read-only. Install/update, rollback, recovery and uninstall require a separate review confirmation; uninstall keeps local data by default. A changed package, installation record, recovery journal or managed private inventory requires a fresh review. A locked, journal-bearing or incompatible queue has an explicitly unavailable count; it is never silently reset. Launch selects and verifies the installed release after setup releases its mutex.

Run uninstall from an external portable setup package, since an installed setup executable cannot remove its own running files. `--install-root` selects a dedicated local binary folder; `--root` is an explicit isolated private-workspace testing override and cannot rebind an owned installation. Native setup does not install/start prerequisites, grant corporate approval, or yet register Windows shortcuts/uninstall entries. Visual/confirmed native action acceptance, fresh-user/machine deployment and signing remain release gates.

Installed binaries include a stable `CommuteCast.exe` at the installation root. Launch it normally for the verified active editor, with `--maintenance` for local backup/restore, or with `--setup` for deployment. Updates and rollback refresh its separately recorded binary through a recoverable journal. `--inspect` reports the selected executable and arguments without starting a child. The self-contained launcher uses its bundled runtime.

`CommuteCast.exe --setup` prepares and verifies a complete external setup copy in `CommuteCast-setup-<installation identity>` beside the installation folder. This requires space for another distribution copy and permits removal of installed binaries while setup runs. It retains a setup-capable release when the editor is rolled back to an older package, and can use the cached kit during interrupted uninstall. The external copy contains distribution files, never narration content; it remains for deployment recovery after uninstall. Reviewed scoped cache cleanup is available as described below. Changed/unowned launchers, ownership records or cached packages are preserved and refused.

## Back up and restore local state

Native setup provides **Clean unused setup copies** after a review. It displays the cache folder, selected copy identities, file count and size, then asks for separate confirmation. Cleanup keeps the active editor/recovery kit, the source running setup, any in-use copy, incomplete distributions and unrecognized entries. Interrupted cleanup is recorded and can be settled with deployment recovery. Narration, installed release folders, exports and Docker artifacts are separate. Empty usage-lease and cache ownership records remain for safe coordination.

The external maintenance tool offers the same operation. Close CommuteCast, inspect the scope, and pass the returned fingerprint explicitly:

```powershell
$review = .\CommuteCast.Maintenance.exe review-setup-cache | ConvertFrom-Json
.\CommuteCast.Maintenance.exe clean-setup-cache --review-fingerprint $review.Fingerprint --confirm-remove-setup-copies
```

Use `--install-root` on both commands for a nondefault installation. After uninstall, a separately extracted portable tool can reclaim all verified unused setup copies. A tool running from a cached copy retains itself. Cached native setup is bound to its recorded installation; use the installed launcher for the editor or private-state maintenance, and a separate portable setup for another installation destination.

Current packages create per-user Start Menu shortcuts for **CommuteCast** and **CommuteCast setup**, inside a folder unique to the installation, and a Windows **Installed Apps** entry. Setup/Uninstall opens the reviewed native setup flow through the stable launcher; local data is kept by default. Owned shortcuts and typed registry values are recorded in a bounded recovery journal. Missing owned entries can be repaired by installing the current package again; altered entries stop setup for inspection. Uninstall removes owned integration before its target binaries and preserves unrelated Start Menu files. Windows Settings display, actual shortcut child launch, corporate signing and fresh-machine acceptance still require direct verification.

Private-data removal requires all durable voice previews to be stopped or reconciled in their original workspace first. Unresolved preview ownership blocks database removal, including resuming a pending uninstall, so preview recovery records remain available. Retain-data uninstall preserves those records and preview files.

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

Run `.\scripts\Test-RestoreCrash.ps1` to exercise actual Windows process termination during restore and recovery; `-Smoke` selects six representative cases. The developer-only fixture host is restricted to isolated `artifacts\installation-acceptance` folders and is never shipped. The harness observes each child's exact live identity, durable journal and exclusive workspace lease before terminating only that retained child handle. Recovery through the packaged maintenance tool compares every managed file's path, length and SHA256, frozen job records, validated synthetic PCM/MP3 bytes, backups, separate exports and unrelated local files. These checks cover process loss at journal/move boundaries; native UI, abrupt machine power loss, sleep/wake and speech-service interruption require separate acceptance.

## Install, update, roll back and uninstall

Settings includes **Check setup**, a read-only inspection that works even when speech is unavailable. The maintenance tool exposes the same result with `check-setup [--root <private workspace>]`; install also reports it before activation. Checks cover Windows x64/build, running runtime, logical processors/physical RAM, supported user/system Docker installation locations and process presence, encoder/probe identity/build, WSL version, CLI, exact local Linux context, bounded daemon access, and verified owned-service health/selected voice. Foreign contexts never reach daemon/service inspection. Tool checks have a five-second allowance; the two independent owned-service inspections run concurrently with 15 seconds each, within the entire report's 30-second allowance. A timed-out inspection does not establish a speech outage: use **Start / repair speech services** for the longer readiness check. The timestamp identifies the report as a snapshot; **Check setup** refreshes it. Setup inspection does not launch Docker, start containers, reset recovery allowance, request speech, install/elevate, or write private state. `scripts/Test-Setup.ps1` verifies absent-folder preservation, private-file checksums and report redaction through the real executable.

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

Binaries stay in immutable verified `releases\<package identity>` folders under `%LOCALAPPDATA%\Programs\CommuteCast`; private data stays in `%LOCALAPPDATA%\CommuteCast`. Installation records bind those separate roots. `--install-root` selects a dedicated local binary folder; `--root` is an explicit isolated-workspace testing override. Installed startup verifies the active release and its bound workspace, including case-insensitive Windows paths. Launching an archived release or one with pending deployment is refused. Run `launch-installed` from the external portable maintenance tool to select the current release, or choose launch in native setup. Automatic prerequisite installation remains separate work.

An update verifies the complete package, snapshots current private state, stages and verifies the binaries, checks/migrates the recognized queue schema, then atomically commits the active release. Rollback verifies the previous binary and pinned snapshot before restoring both; work created after that snapshot moves to retained recovery storage. A recovery snapshot permits undoing the rollback through the same command. Docker images/models are retained separately, so compatible provider availability must also be checked. Recovery follows a bounded journal: before activation it restores the original private state; after activation it retains the committed release. Unexpected or changed deployment records are preserved for inspection.

Uninstall defaults to retaining private data. Explicit removal covers recorded queue/settings/draft/provider recovery, job files and their backup/recovery folders. Exports, provisioning models, Docker artifacts and unknown root files remain separate. Removal locks and checks each file against its recorded checksum, deletes through that same Windows handle, and removes only empty directories. After uninstall commits, interrupted recorded removal resumes through `recover-install`; changed or unrecorded files stop removal for inspection. The small ownership/state/lease records remain to support safe recovery and reinstallation.

`.\scripts\Test-Installation.ps1` runs real packaged commands against separate synthetic fixtures: update, rollback/undo, immutable queued data and PCM checksums, exclusion and both uninstall scopes. `-KeepInstalled` leaves a paused fixture for native checks. `-ActivationCrashCheckpoint Prepared` also terminates an owned activation fixture host at that durable checkpoint and verifies packaged recovery before continuing the suite. Prepared, StateMigrated, BeforeActivation and Activated have passing actual process-loss acceptance with schema-4 data. `-LegacySchemaThree` additionally verifies actual schema 3-to-4 migration: StateMigrated host loss restores schema 3, while Activated host loss retains schema 4 and the committed package. `MigrationBeforeCommit` with `-LegacySchemaThree` verifies packaged recovery after actual host loss inside the schema 3-to-4 transaction. `-RollbackCrashAfterRestore` verifies actual host loss after rollback restores its earlier snapshot and packaged recovery restores the later private state. `-RollbackCrashAfterCommit` verifies the committed rollback retains the earlier package/state and undo restores later jobs. `-UninstallCrashAfterItem` verifies actual host loss after a package file is removed during retain-data uninstall, followed by idempotent packaged recovery and exact private-state preservation. `-PrivateUninstallCrashAfterQueue` verifies actual host loss after private queue deletion, idempotent completion of the recorded removal scope and preservation of exports/models/unrelated files. Earlier legacy versions and other rollback/file/registration interruption checkpoints remain unexecuted. Fixtures stay under ignored `artifacts\installation-acceptance`. Signing, corporate deployment policy, complete prerequisite acceptance and fresh-user/machine acceptance remain release work.

`.\scripts\Test-BulkDeletionCrash.ps1` verifies actual isolated bulk private-deletion host loss after all intents commit and after the first selected record is removed. Seven cases cover private-only, keep-export and remove-export consent, including host loss at the validated exclusive export-removal handle, using checksum-recorded file sentinels. Fresh queue recovery removes selected files/records, honors export consent, preserves exact unselected state and unrelated bytes, and is idempotent. Active speech/publication, audio validity, later deletion-disposition interruption and native deletion acceptance remain separate.

Completed segments remain visible as **X of Y segments completed and validated**, including when a narration stops with an error and when Narration Details is scrolled. **Repair speech & resume** settles any previous reservation before retrying the same job. Retry verifies retained chunk hashes/audio and reuses valid segments; an interrupted segment may need generation again. Docker ownership verification has its own bounded readiness allowance before the eight-second inference-settlement wait, so slow Docker commands do not incorrectly imply inference is still running. The reservation remains until verified settlement.
