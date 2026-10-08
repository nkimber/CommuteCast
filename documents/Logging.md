# Debugging with operational logs

CommuteCast uses [Serilog](https://github.com/serilog/serilog) 4.3.0 and its [file sink](https://github.com/serilog/serilog-sinks-file) 7.0.0 for local structured logging. This fits the WPF application and supports named properties, asynchronous context correlation and bounded rolling files without running another service. The existing SQLite diagnostic history and redacted diagnostic export remain available.

## Find and read a failure

Desktop and launcher logs are under `%LOCALAPPDATA%\CommuteCast\logs`, named `desktop-YYYYMMDD.jsonl` and `launcher-YYYYMMDD.jsonl`, with numeric suffixes when size rotation occurs. Mutating maintenance commands also write `maintenance-*.jsonl`; read-only CLI inspections do not create logs. The location is independent of installation releases and custom narration workspaces, so startup/deployment failures can be recorded before workspace access succeeds.

Each line is JSON. Events include timestamp, level, message template, rendered message and properties. `SessionId`, `ProcessId`, `Component` and `AppVersion` identify a run. `JobId` links narration attempts, stages, chunk counts, failures and tool calls. `ProviderInstance` plus `AdmissionSequence` links a speech attempt to the container's `instance` and `sequence`. HTTP timing measures response headers; job timing covers the whole attempt. A stage event follows a successful checkpoint.

```powershell
$logs = Join-Path $env:LOCALAPPDATA 'CommuteCast\logs'
$events = Get-ChildItem $logs -Filter 'desktop-*.jsonl' |
    Sort-Object Name | Get-Content | ForEach-Object { $_ | ConvertFrom-Json }
$events | Where-Object Level -In Error,Fatal | ConvertTo-Json -Depth 12
# Use the narration ID from Copy all details to reconstruct its timeline.
$events | Where-Object { $_.Properties.JobId -eq '<job ID>' } | ConvertTo-Json -Depth 12
```

When asking Codex to diagnose an issue, provide the approximate occurrence time and narration ID. Codex can read these local files and inspect the matching timeline. Logs are never uploaded automatically or appended to the diagnostic export.

Default level is Information. For extra HTTP/tool timing, set the environment variable before launching the app; it takes effect in new processes:

```powershell
$env:COMMUTECAST_LOG_LEVEL = 'Debug'
.\scripts\Start-CommuteCast.ps1
# Return to the default for subsequent launches.
Remove-Item Env:COMMUTECAST_LOG_LEVEL
```

For an invisible or stalled desktop launch, look for **Desktop build**, **Startup status window shown**, and **Startup step** events in the same session. Each fixed step records start, completion/interruption and elapsed milliseconds; a separate thread logs **still running** every ten seconds even if the UI dispatcher is blocked. The last unmatched step identifies the operation to investigate. Editor Loaded, content-rendered, initialization-completed and exit-code events distinguish startup checks, an unpainted window and later speech readiness. Build version/configuration and debugger attachment identify the binaries and launch context. SQLite primary/extended codes and Windows native error codes supplement safe exception frames without logging private messages.

The startup window shows the current local check, an activity bar and elapsed time. File/recovery/database work runs off the UI thread. **Cancel startup** requests cancellation, then waits for the current operation to settle before releasing storage ownership; it does not force-abandon a restore. Visual Studio **Stop Debugging** can terminate the process without completion/exit events, so an unmatched start alone does not prove a deadlock. If a debugger dialog appears, retain its exact text alongside the timestamp. A watchdog heartbeat identifies a long operation, not its underlying cause.

Each component keeps at most 14 files, rotating daily or after 5 MiB (a final event can exceed that threshold). Writes are unbuffered, avoiding an extra asynchronous queue that could lose recent errors on a crash. Shutdown flushes/disposes the sink. Disk/permission failures must not prevent application startup; bootstrap failures appear in .NET Trace. These are best-effort diagnostics, not a transactional audit journal or a guarantee against power loss. Files are separate from narration backup/restore, cache cleanup and deletion; retained operational job IDs remain until rotation removes their files. Runtime evidence is ignored by Git.

## Speech container diagnostics

Python's standard logging writes JSON to stderr for model loading and synthesis start/completion/failure. No new Python dependency is needed. Read a bounded time window with:

```powershell
docker logs --timestamps --since 30m --tail 200 commutecast-kokoro
docker logs --timestamps --since 30m --tail 200 commutecast-piper
```

Compose limits each container's Docker JSON log to three 5 MiB files. Existing containers need recreation to acquire that logging policy. The changed service code also changes the provider fingerprint: rebuild/reprovision through `scripts/Provision-Speech.ps1 -Build` when adopting it. Previously saved narrations keep their frozen provider identity and must use that original image or be submitted again after reviewing the new provider. This change does not rebuild or replace the running speech services automatically.

## Instrumentation rules

Use constant message templates with named properties (`JobId`, stage, counts, timings and bounded service status). Never log source/script text, titles, pronunciation dictionaries, full job/settings objects, HTTP bodies, subprocess arguments or stdout/stderr. Use `AppLogging.Failure("OperationName", error)` at a catch boundary that absorbs an error. It captures exception types, HResult, HTTP status, method/line frames and a bounded inner chain; messages, Data and source file paths are excluded because they can expose narration or filenames. Python follows the same principle. Keep UI error explanations and existing job failure categories alongside the safe stack diagnostics. Do not mark fatal exceptions handled merely to keep the app alive.

Validation covers JSON parsing, correlation, default filtering, debug enablement, size rotation/retention and private exception-data exclusion. Full regression/build results are recorded in the implementation decisions. Actual WPF crash handling, packaged launch and rebuilt Docker inference require separate acceptance.
