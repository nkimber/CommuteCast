# Acceptance record

This record separates implementation, automated checks, and device/policy acceptance. The application is an implementation candidate, not a blanket assertion that every product requirement has passed release acceptance.

## Automated checkpoint

- .NET 10.0.401 SDK / 10.0.12 Windows Desktop runtime present; WPF solution build passed with zero warnings/errors.
- 38 automated tests passed in Release on October 5, 2026. They include full preparation source-span coverage, maximum-input rejection without truncation, chunk boundary/order/property cases, Unicode surrogate safety, literal dictionary substitution, durable SQLite reopen, crash-after-rename export reconciliation, no-overwrite collisions, cancellation before publication, export failure preserving the private completed file, queued cancellation/retry, stale stage reconciliation, model mismatch refusing inference, checksummed cache corruption recovery, export-only retry preserving synthesis calls, interrupted deletion, ambiguous markup, and empty table cells.
- Docker 29.2.1 local Linux-container context and FFmpeg/FFprobe are installed. Both real candidate services reached ready state and exposed voice/model identity. Runtime inference and GUI acceptance results will be appended after execution.
- Native WPF launch, paste of synthetic input, content-based title, character counter, preparation review, and draft recovery after terminating a development instance were observed. Native shutdown exposed an exception and is under repair; this checkpoint is not release acceptance. The first real-service pilot hit its bounded Docker-command timeout during concurrent image rebuilding. No real end-to-end generation pass is claimed at this checkpoint.

## Requirements implemented

Capture/title/timestamp/durable submission (FR-01–04); deterministic inspectable preparation and source accounting (FR-05–06); engine/voice/pace and audition, literal dictionary (FR-07–08); durable serial queue, real counters, frozen configuration, chunks, one MP3, format/duration/full decode validation, cancel/retry/play (FR-09–17); future-dispatch pause (part of FR-18); user-selected output, verified recoverable publication, naming/metadata, distinct generation/local/cloud states, export retry (FR-19–23); bounded verified local-service readiness and startup without restarting busy models (FR-24–26); process-loss reconciliation and safe exit (FR-27–28); scoped item/all deletion (FR-29–30); actionable failures, redacted diagnostics, verified loopback inference (FR-32–34); About/settings version visibility (FR-36).

## Remaining acceptance and refinements

- **FR-35 / NFR-02,05,13,14:** Actual work-laptop corporate policy, approved voice, representative long-content listening, hardware benchmarks under the user's real workload, corporate OneDrive upload, and Android access/playback with laptop off require device/user evidence. Cloud state remains unknown in the app.
- **NFR-11:** Core automation labels, keyboard commands, resizing/scrolling, light/dark and high-contrast resources exist; screen-reader, 125–200% DPI, high-contrast, and full keyboard-only journeys require manual Windows acceptance.
- **NFR-04 / FR-27:** Automated stage/journal tests exist; real sleep/wake, abrupt host crash, disk-full and locked-folder fault injection remain device acceptance checks.
- **FR-18 (P1):** Pending-job reorder remains a refinement; serial FIFO dispatch and pause/resume are implemented.
- **FR-31 / NFR-15 (P1):** User-controlled deletion is implemented; quota/age-based cache retention and usage accounting remain refinements. Private storage can grow until deleted.
- **FR-08 (P1):** Literal dictionary is supported; additional number/acronym engine controls are not exposed because candidate capabilities are not certified.
- **FR-33 (P1):** Redacted state/counter/version export exists; richer timings, event-transition diagnostics and failure taxonomy remain refinements.
- **NFR-13:** Portable packaging is implemented; signing, corporate installer, fresh-machine install/rollback and complete redistribution legal review remain release gates. Model checksums/image IDs prevent silent model drift.

## Stationary Android pilot checklist

1. Choose the approved corporate local OneDrive folder; submit representative technical material and check the complete local MP3.
2. Audition both engines and listen to the finished file for numbers/acronyms, missing/duplicated material, pace, volume, and joins. Record accepted engine/voice/pace and limitations.
3. Confirm upload using the actual OneDrive client/account. Open the MP3 through the corporate Android account and an allowed player.
4. Switch the laptop off and play the full file. Verify seeking/resume and, if permitted, download and airplane-mode playback.
5. Record chosen player, account restrictions, offline behavior, generation wall time, laptop responsiveness, and release decision. Perform all setup/testing while stationary.

## Git synchronization

The supplied folder had no Git repository or configured GitHub destination. Local work can be committed; publishing awaits the user's existing GitHub repository destination. No private material is published to an invented remote.
