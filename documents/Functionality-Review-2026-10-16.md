# CommuteCast functionality review — October 16, 2026

## Goal and scope

The user's primary workflow is to obtain a GAI-written script and turn it into pleasant, natural, believable audio for commuting. Hosted voices and podcast creation are of interest, with single narration expected to dominate. Optimize sustained listening quality and repeatable voice selection before adding more providers or podcast complexity.

Reviewed source baseline: `46b1dde10a7fc90e43c0a55220561a5410139a04`. This is a source/documentation review with fresh automated checks, not a listening assessment or physical UI walkthrough. No private library, speech services, credentials or provider accounts were changed. Graphify's TypeScript runtime and committed-file inventory were inspected (261 files); no knowledge graph was built or used as evidence. Findings below come from direct source inspection.

## Current functionality

| Area | Implemented behavior | Opportunity |
| --- | --- | --- |
| Script creation | Local prompt builder with topic, goal, audience, duration, style, boundaries and supplied sources; editable prompts; saved briefs; Codex writing guide | Make the prompt-to-paste transition more obvious and preserve edited prompts when rebuilding |
| Dictation | Local microphone recording and Whisper transcription into the listening goal; cancellation and recovery | Useful optional input, secondary to pasted scripts for this workflow |
| Input and preparation | Paste, one-file TXT/Markdown import/drop, title suggestion, code exclusion, pronunciation dictionaries, number/date/acronym profiles, selectable spoken/source review and change highlights | Add explicit URL/reference handling choices and script-readability guidance; retain original source and reversible review |
| Voice selection | Local Kokoro/Piper; hosted OpenAI, ElevenLabs, Cartesia and Gemini adapters; voice refresh; standard, expressive and selected-text auditions | Add a saved comparison session using the same script across candidate voices |
| Delivery | Pace, supported hosted delivery/emotion settings; local natural phrasing, sentence/paragraph pauses, Kokoro blends and Piper variation | Make hosted delivery settings readily accessible beside the draft; provide curated, editable delivery profiles |
| Defaults and presets | Saved narration defaults, per-engine remembered voices, three starter presets and named presets | Hosted model/delivery/emotion are outside the preset/default options snapshot; see finding 2 |
| Podcasts | Two-to-five speakers, formats, personalities, provider voice bindings, strict labelled dialogue validation, speaker balance and optional joint blocks | Improve validation navigation and pasted-dialogue repair suggestions after the core narrator workflow is polished |
| Generation | Durable queue, ordering, pause future jobs, cancellation, resumable validated segments, immutable settings and provider-attempt history | Improve hosted continuity across segments; retain explicit handling of uncertain billable requests |
| Audio output | Ordered PCM assembly, final MP3 encoding and full decode/probe validation, metadata, local export, natural-local final loudness processing | Apply a measured final loudness policy to hosted single narration; test joins and pronunciation by listening |
| Library and revisions | Title search, status filters, sorting, saved source/script/prompt inspection, new drafts and linked narration versions, audio duration/size, folder actions, bulk deletion | Add voice/version comparison and eventually tags or script-content search if library size warrants them |
| Playback and delivery | Pause/continue, seeking, skip backward/forward, notifications, export folder actions and per-file Windows cloud-sync observation | Persist listening position; confirm actual phone/car workflow. Cloud metadata cannot establish phone playback |
| Recovery and operations | Startup progress, speech repair, guarded Docker restart, suspend/wake recovery, storage budgets/cache cleanup, redacted diagnostics and logging | Consolidate recovery entry points and use task-focused guidance before exposing detailed diagnostics |
| Maintenance and distribution | Private backup/restore, schema compatibility, package verification, per-user install/update/rollback/uninstall, portable publication and Windows CI | Provide a concise current-build acceptance summary; existing detailed records contain many historical scopes |

## Prioritized findings

### 1. Improve hosted long-form continuity — highest audio-quality priority

`QueueCoordinator` generation setup uses `Chunker.Split(job.Prepared.Script, 450)` for non-natural single narration. `NaturalChunker.IsNatural` applies only to Kokoro/Piper, so hosted single narration uses these short independent requests even when the provider's configured text limit is larger. `HostedSpeechProvider.BuildRequest` supplies the current segment without surrounding script or previous-request conditioning. `AudioPipeline.GapSamples` adds a fixed 150 ms gap after a period or newline, without subtracting existing hosted boundary silence.

These facts identify a plausible cause of frequent prosody resets and uneven joins; they do not establish audible defects in untested output. Evaluate paragraph-aware, provider-specific segment sizes, contextual conditioning where supported, and silence-aware joins. Preserve immutable manifests, receipts, explicit uncertain-request handling and compatibility with existing jobs. Larger requests trade more context for a larger regeneration unit.

ElevenLabs explicitly documents request stitching to maintain prosody across requests, including previous request IDs and a two-hour validity limit. Its guide excludes `eleven_v3`; capability support must be model-specific rather than applied to all models. Source: [ElevenLabs request stitching](https://elevenlabs.io/docs/eleven-api/guides/how-to/text-to-speech/request-stitching), consulted October 16, 2026.

Acceptance: compare the same real script before/after, including paragraph transitions and resumed generation. Require exact script coverage, successful retry/reuse and listener preference; smooth joins cannot be inferred from checksums or valid MP3 decoding.

### 2. Save the complete hosted narrator recipe — concrete behavior gap

`NarrationPreset` stores `NarrationOptions`. That record contains engine, voice, speed, code handling, pronunciation and local delivery, but no hosted `SpeechConfiguration`. `NarrationPreferences.UsingDefaults` compares that same options record. Hosted model, delivery instructions and emotion instead live in the separate per-engine `SpeechDefaults` dictionary.

Consequently, saving a named preset does not capture its hosted model/delivery/emotion, and applying it does not restore those fields. The defaults indicator can also report a match while those hosted fields differ. Per-engine settings persistence exists, but it cannot represent two named recipes using different hosted models or styles on the same provider.

Extend presets and default comparison to capture the full hosted recipe, with backward-compatible loading and model/voice validation. Acceptance: save two recipes on one provider with different models/styles, switch between them, relaunch, and verify every displayed and submitted setting matches the chosen recipe.

### 3. Add a voice comparison workflow — highest product-value priority

Current auditions already support standard, expressive and selected script text, so basic preview is present. What is missing is a convenient comparison session: identical text, several candidate voices/settings, retained clips, easy switching, preference notes and saving the winner as a complete narrator recipe.

Start with a representative 60–90-second script containing normal explanation, a question, a proper name, numbers and a paragraph transition. Follow the shortlist with a 10–15-minute continuous sample to assess fatigue, repetition and consistency. Make charges visible before starting hosted comparisons and avoid generating every provider automatically. No particular provider is declared the quality winner without the user's listening judgment.

Acceptance: the user can choose a preferred voice and reproduce it on a second script without navigating through technical settings.

### 4. Make hosted single-narration loudness consistent

`AudioPipeline.NormalizeAsync` applies a loudness filter to non-natural podcasts, while the measured final assembled loudness pass is conditional on `NaturalChunker.IsNatural`. Hosted single narration receives format conversion but no explicit final loudness matching. Provider levels can therefore carry through to the MP3.

Evaluate a final measured loudness/true-peak pass for hosted single narration, preserving expressive dynamics. Compare it in the actual listening environment before choosing the target. This is a consistency improvement; normalization does not make robotic prosody expressive.

### 5. Simplify the everyday creation path

The application exposes extensive settings and multiple save/default actions. Hosted delivery controls are in Settings, although their effect is central to narration quality. Settings and Compose also share the selected provider through the same view model.

Make the main path: paste script → choose saved narrator → preview representative passage → create MP3. Show provider/model and relevant delivery settings together, with an advanced section for infrequent controls. Clearly distinguish draft-only changes, named recipes and saved defaults. Consider a preview option using the exact production segmentation/join policy: a short audition alone does not establish long-form continuity.

### 6. Close the live hosted acceptance gap

Existing documentation explicitly separates adapter contract tests from live hosted synthesis, account permissions, billing reconciliation and listening approval. `ReadyAsync` checks model metadata for OpenAI/Gemini and voice listings for ElevenLabs/Cartesia; these checks do not execute the synthesis route or establish that every configured model works for the account.

Provide separate statuses for credential/catalog checks and a successful synthesis sample, including its model and timestamp. Run bounded, explicitly chosen live trials against configured accounts; record request IDs, actual returned audio and any available usage. Do not treat a model appearing in the built-in catalog or a synthetic transport test as live acceptance. This review did not exhaustively revalidate every provider's current API contract.

### 7. Improve the listening loop

`PlaybackController` supports seeking and pause, but `Stop` resets playback state and no persistent per-item listening position is implemented there. Add continue-listening across relaunch, then consider bookmarks and independent playback speed if desktop listening matters. These are lower priority if all listening occurs in a phone player; establish that destination first.

For scripts with isolated bad pronunciations, consider a later passage-level repair preview/regeneration workflow. Existing versions regenerate a separate narration; they are not selective passage replacement. Any future repair must rebuild and validate a complete output with explicit version provenance.

## Recommended sequence

1. Test a small hosted voice shortlist on the same representative script, then on a continuous commute-length passage. Establish a preferred sound before expanding features.
2. Capture complete hosted recipes in presets/defaults and expose their delivery controls beside the draft.
3. Improve hosted segmentation/context/joins, using listener comparisons and existing durable recovery guarantees as acceptance criteria.
4. Add final hosted loudness matching and a concise end-to-end readiness/quality summary.
5. Polish podcast validation and listening-library extras according to actual usage.

## Fresh validation

- Complete desktop test project, Release: **50 passed, 0 failed, 2 skipped, 52 total**. Skipped cases require opt-in live Whisper/native binding and physical microphone capture. Duration reported by test runner: 33 seconds.
- Focused backend run, Release: **114 passed, 0 failed, 0 skipped**, covering narration prompts, podcast parsing, provider models, narration tools, library queries, recovery guidance, generation prompts, version storage, preferences, local voice quality and dictation policy. Duration reported by test runner: 3 seconds.
- Both runs compiled their required projects. A complete solution build, full backend regression, fresh Python service tests, physical UI interaction, live provider synthesis/billing, listening evaluation and phone/car delivery were not executed for this review.
- Ignored TRX files: `tests/CommuteCast.Desktop.Tests/TestResults/functionality-review.trx` and `tests/CommuteCast.Tests/TestResults/functionality-review.trx`.

## Open questions

- Which hosted accounts are already available, and what monthly or per-episode budget is comfortable?
- Which accent and delivery would be most enjoyable: conversational explainer, documentary narrator, or a more animated storyteller?
- What is a typical episode length, and which phone/player/car connection is used?
- Would optional direct script generation inside CommuteCast be valuable, or is copying from a chosen GAI tool the preferred workflow?

These questions affect implementation order; they do not block the findings above.
