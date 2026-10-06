# Verification matrix

October 5, 2026. This audits the PRD's T-01–T-30 catalog; its FR/NFR traceability maps to these tests. **No catalog row is treated as fully accepted solely because a narrower automated test passes.** The full goal remains open.

Current reproducible commands: `dotnet build CommuteCast.slnx -c Release --no-restore` (zero warnings/errors), `dotnet test CommuteCast.slnx -c Release --no-build` (61 passed), and `scripts/Publish-Portable.ps1` (self-contained x64 package produced). Native launch/settings/draft/usage/theme/exit and an older pinned Piper image's synthetic export have observed evidence. The current locked image has not been rebuilt/accepted because Docker Desktop cannot initialize its stale runtime socket.

| Catalog test | Current evidence | Still required for the full pass condition |
| --- | --- | --- |
| T-01 | Source capacity/unit invariants; async submit; native synthetic paste, recovered draft and Ctrl+Enter validation | Native maximum-input paste/undo, persistence-failure UI, successful repeated-click/focus journey under load |
| T-02 | Title/name safety and immutable timestamp tests | Empty-title fallback, exhaustive DST/offset and metadata readback fixtures |
| T-03 | Exact source partition; markup/code/table/link/dictionary/exclusion fixtures; native review | Full release corpus and listening comparison |
| T-04 | Contiguous chunk/property/Unicode/long-token tests | Broader abbreviation/decimal/CRLF corpus review |
| T-05 | Settings fingerprints, incompatible model rejection, corrupt-cache and export-only retry tests | Every provider/normalizer/format version change contract and duplicate-submit UI acceptance |
| T-06 | Both services expose candidate voices; audition UI exists | Actual user's short and 20–30 minute listening approval |
| T-07 | Piper real WAV-to-MP3/export pipeline; initial Kokoro readiness | Current locked-image contract tests for both engines, technical corpus, limits and listener approval |
| T-08 | Bounded host process/HTTP/size/cancellation implementation; process timeout/cancellation tests | Injected disconnect/429/5xx/partial/late HTTP response and active-request integration races |
| T-09 | Bounded launch/readiness and native actionable exhausted state | Successful stopped-Desktop/cold-daemon and prerequisite matrix on Windows |
| T-10 | Exact local pipe tests; image/labels/binding checks in provider | Automated complete container-identity fixtures and actual foreign-name/occupied-port/stopped-owned tests |
| T-11 | Loading/active wait guard; persisted readiness allowance | Slow cold load, missing voice/model, unload and repeated-job integration |
| T-12 | Persisted exhausted-budget/reset tests; OOM/identity guards | Busy/unhealthy/incompatible/OOM matrix and shared outage acceptance on real Docker |
| T-13 | Durable reopen, stale-stage, corrupt/missing cache tests; native draft recovery | Real chunk-process kill, service kill, sleep/wake and stale response tests |
| T-14 | Synthetic tone pipeline, coverage/order and duration checks | Distinct ordinal marker fixtures that deliberately omit/duplicate/reorder audio |
| T-15 | Complete MP3 probe/decode/duration in pipeline and Piper pilot | ID3 readback, measured frames, calibrated clipping/silence and every-join listening |
| T-16 | Queued cancellation, pre-publication cancellation, compatible retry | Running/chunk/encoding/copy/rename race matrix and native cancel journey |
| T-17 | Filename safety, collisions/no overwrite, identity-bearing filenames | Concurrent jobs and actual corporate destination metadata/path acceptance |
| T-18 | Crash-after-rename reconciliation and retained export failures | Faults at every staging boundary plus actual OneDrive temporary-file observation |
| T-19 | Missing destination, export-only retry, admission reservations, checkpoint failure tests | Actual full disk, read-only/locked destination and UI recovery matrix |
| T-20 | Idempotent publication, collision and changed-export deletion guard tests | Exhaustive journal/filesystem combinations and moved-file UI acceptance |
| T-21 | Local-only baked-model architecture and honest cloud-unknown display | Disconnected actual synthesis/export, paused sync and reconnect without regeneration |
| T-22 | Interrupted tombstones, owned-export/unrelated-file guards, cleanup protection tests | Running/retry/export/cleanup delete races and itemized native one/all acceptance |
| T-23 | SQLite reopen, checkpoint-failure dispatch pause, retry transactional state, per-user mutex | Corruption, backup/migration/restore, second-instance UI and deployment integration |
| T-24 | Structured bounded diagnostic history/timings/categories; content/path redaction and event deletion tests | Provider echo, container-log and manifest manual review on target setup |
| T-25 | Typed arguments, bounded output, safe identity/path checks, PCM validation, proxy/redirect disabled | Full malformed WAV/reparse/markup/provider-identity adversarial matrix |
| T-26 | Native editor/settings/review/scrolling/theme/usage/exit; focus/hover resource repair | Narrator, high contrast, supported DPI, resizing and full keyboard/cancel/load journeys |
| T-27 | Piper 1,526-character/4-chunk pilot measured under contention | Actual laptop CPU/work-app corpus, cold/warm timings, resource/thermal/battery/UI latency and agreed thresholds |
| T-28 | Self-contained portable build, ZIP contents and native packaged launch | Corporate signed installer choice, fresh-user/machine, prerequisite checks, queued upgrade, backup/rollback/uninstall and complete notices |
| T-29 | Local verified MP3 exists; cloud status unknown | Corporate upload and actual Android full playback/seek with laptop off; approved player/account and observed offline/background/resume |
| T-30 | Implementation checkpoints and synthetic evidence recorded | Multi-day representative 20-job pilot, planned faults, issue closure and user approval |

## Remaining implementation work

Prioritize complete provider transport/identity fault fixtures, the running cancellation/publication/deletion race matrix, ordinal audio/metadata fixtures, and deployment backup/update/rollback support. P1 dictionary currently provides versioned literal substitutions and preserves numbers for the engine; unsupported number/acronym options need clearer capability presentation. Corporate signing/IT approval, voice judgment, and physical Android/laptop-off acceptance require external decisions/evidence.

Docker recovery is an external prerequisite now: Desktop displays an inaccessible `dockerInference` endpoint; automatic review rejected the attempted runtime repair as “blocked by policy.” No factory reset, global prune, image/volume deletion, or machine reboot was performed. GitHub synchronization remains pending an authorized existing upstream; completed increments have local commits and each push was attempted immediately.
