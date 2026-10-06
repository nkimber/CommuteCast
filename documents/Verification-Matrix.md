# Verification matrix

October 6, 2026. This audits the PRD's T-01–T-30 catalog; its FR/NFR traceability maps to these tests. **No catalog row is treated as fully accepted solely because a narrower automated test passes.** The full goal remains open.

Current reproducible commands: `dotnet build CommuteCast.slnx -c Release --no-restore` (zero warnings/errors), `dotnet test CommuteCast.slnx -c Release --no-build` (227 passed), and `scripts/Publish-Portable.ps1` (earlier self-contained x64 package produced). Native launch/settings/draft/usage/theme/exit and an older pinned Piper image's synthetic export have observed evidence. The current locked image has not been rebuilt/accepted because Docker Desktop cannot initialize its stale runtime socket; the October 6 authorized restart also hung.

| Catalog test | Current evidence | Still required for the full pass condition |
| --- | --- | --- |
| T-01 | Source capacity/unit invariants; async submit; native synthetic paste, recovered draft and Ctrl+Enter validation | Native maximum-input paste/undo, persistence-failure UI, successful repeated-click/focus journey under load |
| T-02 | Title/name safety and immutable timestamp tests | Empty-title fallback, exhaustive DST/offset and metadata readback fixtures |
| T-03 | Exact source partition; markup/code/table/link/dictionary/exclusion fixtures; native review | Full release corpus and listening comparison |
| T-04 | Contiguous chunk/property/Unicode/long-token tests | Broader abbreviation/decimal/CRLF corpus review |
| T-05 | Settings/preparation/chunk/audio fingerprints, incompatible model rejection, corrupt-cache and export-only retry tests | Duplicate-submit UI acceptance and real provider upgrade regression |
| T-06 | Both services expose candidate voices; audition UI exists | Actual user's short and 20–30 minute listening approval |
| T-07 | Piper real WAV-to-MP3/export pipeline; initial Kokoro readiness | Current locked-image contract tests for both engines, technical corpus, limits and listener approval |
| T-08 | Adapter fault tests inject disconnect, 429/500/502/503/504, permanent errors/redirects, bad MIME/PCM/truncation/length/size, cancellation/late response and noncooperative read; two transient retries under one deadline; attempt cleanup and prior-output preservation | Real-provider disconnect/cancellation races and observed server quiescence |
| T-09 | Bounded launch/readiness and native actionable exhausted state | Successful stopped-Desktop/cold-daemon and prerequisite matrix on Windows |
| T-10 | Container fixtures cover exact image/name/project/owner/contract, binding/extra ports, command/entrypoint/engine, network/mount/resources/privileges, OOM/paused/restarting; foreign metadata never reaches HTTP/start; owned start is reinspected | Actual foreign-name/occupied-port/stopped-owned tests |
| T-11 | Loading/active wait guard; persisted readiness allowance | Slow cold load, missing voice/model, unload and repeated-job integration |
| T-12 | Persisted budget/reset, one daemon launch/exhaustion, loading/busy no-restart, incompatible health/voice/fingerprint and OOM guard fixtures | Shared outage/resource acceptance on real Docker |
| T-13 | Durable reopen, stale-stage, corrupt/missing cache tests; native draft recovery | Real chunk-process kill, service kill, sleep/wake and stale response tests |
| T-14 | Three distinct ordinal tones checked after MP3 decode; deliberate missing/duplicate/reorder/ordinal/offset/receipt/sample-count/corruption defects block completion | Full real-voice corpus and every-join listening |
| T-15 | Complete MP3 probe/decode/duration; 79,200 measured decoded frames; Unicode title/artist/year/full UTC/job ID readback; strict complete RIFF fixtures | Calibrated clipping/silence thresholds and every-join listening |
| T-16 | Durable cancellation/relaunch/retry/persistence-failure fixtures; running readiness/second-chunk and copy/pre-rename/post-rename barriers; compatible receipts retained and settled cancellation prevents dispatch | Actual encoding interruption, host-kill/sleep and native cancel journey |
| T-17 | Filename safety, collisions/no overwrite, identity-bearing filenames | Concurrent jobs and actual corporate destination metadata/path acceptance |
| T-18 | Intent/exclusive-create/copy/flush/verify/pre-rename/post-rename/DB-commit faults; locked source, no-overwrite collision, known partial recreation and final/partial reconciliation fixtures | Host-kill at creation-before-ownership checkpoint (unknown staging is preserved), real disk-full/locks and OneDrive temporary-file observation |
| T-19 | Missing destination, export-only retry, admission reservations, checkpoint failure tests | Actual full disk, read-only/locked destination and UI recovery matrix |
| T-20 | Idempotent publication, collision and changed-export deletion guard tests | Exhaustive journal/filesystem combinations and moved-file UI acceptance |
| T-21 | Local-only baked-model architecture and honest cloud-unknown display | Disconnected actual synthesis/export, paused sync and reconnect without regeneration |
| T-22 | Interrupted tombstones; running readiness/second-chunk/copy/pre-rename/post-rename delete barriers; remove-export/keep-export scopes and unrelated-file preservation; relaunch does not resurrect deleted work | All-item/retry/cleanup overlap and itemized native one/all acceptance |
| T-23 | SQLite identity/version/history and corrupt/foreign/future refusal; legacy pre-migration snapshot; migration fault/cancellation rollback; real SQLite transaction failure; WAL-aware backup versus plain copy; isolated DB restore/reopen/write; draft recovery; thread-independent exclusive workspace lease and per-user mutex | Full workspace restore/crash recovery, native corruption/second-instance UI and deployment integration |
| T-24 | Structured bounded diagnostic history/timings/categories; content/path redaction and event deletion tests | Provider echo, container-log and manifest manual review on target setup |
| T-25 | Typed arguments, bounded output, safe identity/path checks, PCM validation, proxy/redirect disabled | Full malformed WAV/reparse/markup/provider-identity adversarial matrix |
| T-26 | Native editor/settings/review/scrolling/theme/usage/exit; focus/hover resource repair | Narrator, high contrast, supported DPI, resizing and full keyboard/cancel/load journeys |
| T-27 | Piper 1,526-character/4-chunk pilot measured under contention | Actual laptop CPU/work-app corpus, cold/warm timings, resource/thermal/battery/UI latency and agreed thresholds |
| T-28 | Self-contained portable build, ZIP contents and native packaged launch | Corporate signed installer choice, fresh-user/machine, prerequisite checks, queued upgrade, backup/rollback/uninstall and complete notices |
| T-29 | Local verified MP3 exists; cloud status unknown | Corporate upload and actual Android full playback/seek with laptop off; approved player/account and observed offline/background/resume |
| T-30 | Implementation checkpoints and synthetic evidence recorded | Multi-day representative 20-job pilot, planned faults, issue closure and user approval |

## Remaining implementation work

Prioritize full workspace backup/restore and deployment update/rollback support, followed by remaining host-crash/encoder/all-item/cleanup race coverage. SQLite migration/snapshot foundations are tested; full deployment acceptance is not established by an isolated DB restore. Provider transport/identity, ordinal audio/metadata and publication/cancellation boundary fixtures are implemented; real-service regression and clipping/silence calibration remain open. Unrecognized staging from legacy jobs or a crash before its ownership checkpoint requires inspection; automatic orphan recovery remains a refinement. P1 dictionary currently provides versioned literal substitutions and preserves numbers for the engine; unsupported number/acronym options need clearer capability presentation. Corporate signing/IT approval, voice judgment, and physical Android/laptop-off acceptance require external decisions/evidence.

Docker recovery is an external prerequisite now: Desktop displays an inaccessible `dockerInference` endpoint; automatic review rejected the attempted runtime repair as “blocked by policy.” No factory reset, global prune, image/volume deletion, or machine reboot was performed. GitHub synchronization remains pending an authorized existing upstream; completed increments have local commits and each push was attempted immediately.
