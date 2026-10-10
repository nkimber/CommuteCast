# Multi-provider TTS and podcast plan

**Recorded:** October 10, 2026

**Status:** Agreed product direction; implementation and listening acceptance pending

**Scope:** Extend the native CommuteCast application with local and hosted speech providers, single-voice narration and podcasts with two to five speakers.

This is a development plan, not a description of available features. The current application supports local Kokoro and Piper with one captured voice per narration. Its topic prompt builder produces narration-only writing prompts. Hosted TTS, podcast parsing, speaker libraries and podcast formats are not implemented.

## Product decisions

1. Retain local TTS and add ElevenLabs and OpenAI as initial hosted providers. Make additional providers possible through the same adapter contracts.
2. Include Cartesia and Google Gemini in the next provider evaluation/integration wave. Select additional services through listening and operational acceptance rather than assuming every advertised model is production-ready.
3. Keep text generation outside the application: build and copy a prompt, use the user's chosen LLM, paste its output back, validate and review, then generate one MP3. Direct LLM text-generation integration is not part of this increment.
4. Support both pasted single-voice narration and structured dialogue. Podcast mode supports two to five speakers, reusable personalities and reusable episode formats.
5. Keep local synthesis available without a hosted account. Hosted synthesis requires a deliberately selected provider and configured credentials; a local failure never dispatches text to a hosted service automatically.
6. Select one provider/model for an episode initially, with a separate voice and delivery profile for each participant. Mixing providers within one episode can be considered after consistent audio and cost behavior are proven.
7. Keep the LLM response free of preambles, references, production notes and appendices. In podcast mode, the only additional control text is the agreed speaker label before each turn; the application removes that label before speech generation.

## Provider scope and evidence

The following capabilities were reviewed against official sources on October 10, 2026. They establish integration options, not a listening-quality ranking. Model availability, limits, voice access and pricing must be rechecked when implementing each adapter.

| Provider | Planned place | Rendering approach | Material constraints |
| --- | --- | --- | --- |
| Local Kokoro / Piper | Required retained baseline | Individual turns with installed voices | Current adapter exposes voice and speed, without expressive instructions. Preserve existing local jobs, provisioning and recovery. |
| ElevenLabs | Required initial hosted provider; first dialogue audition | Individual narration and multi-speaker dialogue blocks | Text to Dialogue assigns a voice to each turn and supports more than five speakers. The documentation recommends at most 2,000 total text characters per request for reliable generation; this is guidance, not an assumed universal hard limit. Delivery tags and output may vary between generations. [Official documentation](https://elevenlabs.io/docs/overview/capabilities/text-to-dialogue). |
| OpenAI | Required initial hosted provider | Individual turns with voice and separate delivery instructions | `gpt-4o-mini-tts` supports instructions for emotion, intonation, accent, pace and tone. The current speech endpoint selects one voice per request; CommuteCast owns turn assembly. [Official documentation](https://developers.openai.com/api/docs/guides/text-to-speech). |
| Cartesia | Next hosted integration target | Individual turns with voice and supported generation controls | Sonic provides speed, volume and emotion guidance; emotion controls are marked beta. Apply capability-specific settings per request. [Official documentation](https://docs.cartesia.ai/build-with-cartesia/capability-guides/volume-speed-emotion). |
| Google Gemini | Next hosted integration target | Native dialogue where supported; individual turns for larger casts | The current TTS documentation limits single-request dialogue to two speakers with prebuilt voices. A three-to-five-speaker episode, or dialogue using custom voices, needs individual-turn rendering. [Official documentation](https://ai.google.dev/gemini-api/docs/speech-generation). |
| Local Qwen3-TTS | Candidate for a later expressive local engine | Individual turns; evaluate fixed voices and reusable designed voices | Different variants support instructed delivery, voice design and reusable voice references. Presets have only two native English voices; five-speaker English quality and hardware performance need a feasibility trial. [Official repository](https://github.com/QwenLM/Qwen3-TTS). |

Hosted providers receive the spoken text and incur provider usage charges, including applicable audition/regeneration charges. This extends the original local-only synthesis scope; local source/history storage and existing export behavior continue to apply. Adding an adapter does not enable it or create a provider account automatically.

## Speaker and format libraries

A speaker personality stores a stable application ID, display name, areas of expertise, writing style, conversational tendencies and default delivery guidance. Expertise is a writing role, not evidence that generated statements are correct.

Voice bindings are separate from personality: store provider ID, model where necessary, exact provider voice ID, language/accent and supported pace/delivery settings. The same personality can have different bindings for local, ElevenLabs or OpenAI. An absent binding needs selection and audition; do not guess a replacement voice.

A format defines permitted participant counts, roles, discussion structure and interaction rules. Initial examples are two presenters, a presenter interviewing a guest, one presenter with two to four experts, and two presenters with one to three guests. Formats guide the writing prompt; they do not require a particular TTS service.

An episode captures its topic, audience, duration, chosen format, cast, exact labels, voice bindings and delivery settings. Capture copies of the selected library entries so subsequent library edits do not change an existing prompt, pasted-script contract or queued job.

## Application workflow

1. Choose **Narration** or **Podcast**, and paste existing text or build a topic prompt. For a podcast, choose format, cast and provider, then bind and audition each voice.
2. Build a prompt from the captured brief and expected cast. Copy it to the chosen LLM; text generation remains manual.
3. Paste the result into the episode editor. Podcast parsing produces an ordered list of turns with source locations, speaker IDs and spoken text. Each new turn starts with an exact configured `Speaker: dialogue` label; continuation lines belong to that turn. Reject text before the first label, unknown or ambiguous labels, empty turns, invalid Unicode and exceeded application/provider bounds.
4. Report structural errors with line locations. Check required participation from the format, flag suspected references, headings and production notes, and show approximate length and speaker balance. These are editorial warnings, not proof of factual accuracy or exact spoken fidelity.
5. Review the actual spoken text, voice assignments and supported delivery settings. Strip speaker labels from the speech payload. Changing text, cast, format or speech settings invalidates the preceding validation/render preview.
6. Freeze the validated episode and deterministic render plan when queued. Generate, validate and assemble in order, then publish one completed MP3 through the existing export pipeline.

The prompt builder and dialogue parser share a versioned episode contract. Preserve stale or edited drafts, indicate when rebuilding is needed and do not silently overwrite pasted work. Migrate older narration drafts/jobs without treating them as podcasts or changing their captured fingerprints.

## Provider architecture

### Registry and capabilities

Introduce a provider registry and provider-specific adapters behind a common application service. The initial implementation can use registered built-in adapters; dynamic third-party plugin loading is unnecessary.

Each provider advertises capabilities for its selected model: local/hosted execution, single-turn rendering, dialogue-block rendering, maximum speakers per block, input limits, supported languages and voices, delivery instruction/tag support, pronunciation support, accepted output formats and model-version identity. Track credential/readiness state separately from synthesis capability.

Optional features such as word/turn timestamps, request lookup, idempotency and usage reporting are advertised only when verified. Do not infer them from the presence of an HTTP API. The UI exposes compatible controls and explains unavailable settings rather than putting unsupported instructions into spoken text.

### Rendering modes

Define an immutable render unit as one turn, part of a long turn, or a block of adjacent dialogue turns. Keep ordered turn/source mappings alongside every unit.

- **Turn rendering:** select the captured speaker voice and delivery settings; split long turns at semantic boundaries within the selected provider's limits.
- **Dialogue rendering:** send several adjacent turns, each with its captured voice, to a provider that supports that cast and mode. Split at turn boundaries where possible and retain the provider's timing within each returned block.
- **Compatible fallback:** when the selected model cannot render the whole cast jointly, use its individual-turn mode if supported. This changes rendering strategy within the selected provider; it does not switch services or voices.

Delivery guidance lives outside the copied spoken script. Adapters translate it into a separate instructions field, structured settings or supported internal tags. Tags are never forwarded literally to an engine that would read them aloud. Use restrained defaults and allow a short per-turn delivery override where supported.

For the first podcast release, use sequential turns. Natural overlap, backchannels and non-speech performance events are later features with explicit support and transcript-review rules.

### Immutable identity and audio

Capture provider/model selection, an immutable model revision when available, voice bindings, delivery settings, contract versions, normalized text, render-unit boundaries and join policy in job/cache identity. Library edits, provider defaults or account configuration changes must not silently alter queued work. Credential values never enter the job fingerprint.

Cloud providers may expose mutable model aliases rather than verifiable weights. Record the requested model/snapshot and returned revision when available; do not manufacture an image fingerprint or promise byte-identical regeneration. If a captured voice/model disappears, preserve existing audio and report the unavailable configuration. A changed provider/model/voice creates a revised job.

Normalize each returned audio unit through the existing PCM pipeline before assembly. Preserve ordered coverage and checksum receipts. Use a versioned podcast join policy: retain internal dialogue timing, avoid adding a fixed narration pause after every small turn, and normalize loudness for listening in a car. Do not concatenate container headers or separately encoded MP3 bytes as if they were raw audio.

Cache completed validated audio so retries reuse it. Regenerating a dialogue block replaces that entire block unless verified timestamps and an implemented editing contract make smaller replacements safe.

### Credentials, cost and recovery

Store hosted credentials through Windows user-protected secret storage, with only an opaque credential reference in ordinary settings. Exclude secrets from drafts, job payloads, MP3 comments, diagnostics, backups and Git. Restore should explain which hosted connections need credentials again. Keep provider failure messages free of authorization headers and private source text.

Show the selected provider/model, local or hosted execution, and estimated usage/cost before generation and paid auditions when a current rate model is available. Separate estimates from provider-reported usage; record the rate date, currency and assumptions. Do not report unavailable cost data as zero or guarantee a provider bill.

Use provider-specific bounded retry rules for rate limiting, transient failures, authentication and quota errors. Honor provider backoff and preserve validated units. A timed-out or cancelled cloud request may still run or be billed remotely: reconcile using supported request lookup/idempotency when available, otherwise retain an uncertain attempt state rather than claiming remote cancellation or exactly-once billing. Explicit retry can create another billed attempt.

Local reservation/settlement remains specific to the local adapter. A hosted-only job must not require Docker readiness, start a local container, or be blocked by a local-only reservation. Continue using the shared queue and durable file ownership/checkpoint rules; define cancellation and resume behavior separately for hosted attempts.

## Changes needed in the existing application

| Area | Current implementation | Planned change |
| --- | --- | --- |
| Core models | `NarrationSettings` and `Job` capture one engine/voice; `TextChunk` has no speaker | Add versioned episode/cast/turn/render-unit snapshots and provider capabilities; preserve legacy serialization and fingerprint behavior. |
| Provider contracts | `ISpeechProvider` supports ready/synthesize; local extensions handle durable writes and status | Add registry, discovery, readiness, turn and optional dialogue operations without requiring Docker process/image fields from hosted adapters. |
| Queue | `QueueCoordinator` dispatches every chunk with `job.Settings` | Dispatch immutable render units with the appropriate speaker/block configuration; keep resumable ordered receipts and export validation. |
| Desktop | `MainViewModel` depends directly on `LocalSpeechProvider`; lifecycle/setup probe local engines | Depend on provider services, scope setup and wake checks to required providers, and add hosted configuration, casts and episode review. |
| Prompt/draft | Narration-only template and saved topic brief | Add podcast contracts, exact-label prompting, format/personality snapshots, validation and stale-state handling. |
| Audio/provenance | One-voice metadata and a fixed narration join contract | Add podcast timing and multi-voice provenance without embedding source text, delivery instructions or secrets in MP3 comments. |
| Maintenance/diagnostics | Backup, estimates and diagnostic filtering assume current local settings | Migrate nonsecret provider/cast data; preserve original local records; distinguish hosted usage and model identity from local image/readiness evidence. |

## Delivery sequence

| Increment | Deliverable | Completion evidence |
| --- | --- | --- |
| 1. Provider foundation | Registry/capabilities, versioned settings and provider-scoped setup; existing Kokoro/Piper through the new boundary | Legacy migration/cache/default regressions; no local behavior loss; hosted-only setup tested without Docker using fixtures. |
| 2. Hosted narration | ElevenLabs and OpenAI adapters, credential configuration, voice discovery, audition, usage display and durable attempts | Provider contract/failure fixtures; explicit live short samples and full MP3 checks for each configured provider; actual cost/usage limitations recorded. |
| 3. Podcast contracts | Cast/personality/format libraries, prompt generation, pasted-dialogue validation and turn-rendered two-to-five-speaker jobs | Shared prompt/parser contract tests; captured-library and draft tests; ordered multi-voice rendering and resume checks; native workflow validation. |
| 4. Expressive dialogue | ElevenLabs dialogue blocks, supported per-turn delivery controls, podcast joins and block regeneration | Block boundary/cast/limit tests; cancellation/retry evidence; comparative two- and five-speaker listening acceptance. |
| 5. Provider expansion | Cartesia and Gemini adapters; evaluate an expressive local engine and further services | The same adapter acceptance suite plus provider-specific limits, recovery and representative listening checks. |

Commit and push each completed, appropriately validated increment with honest acceptance limits. Do not enable unfinished adapters in the production selector.

## Acceptance and provider admission

- Existing local single-voice drafts, defaults, jobs, cached receipts, cancellation and export still work after migration.
- Local generation works without hosted credentials; hosted-only generation works without Docker. Selected provider/model/voices remain fixed throughout an episode and after relaunch.
- Every allowed turn appears once in order; labels and output scaffolding are absent from synthesis payloads. Source coverage and audio validation do not claim exact spoken fidelity.
- Unknown labels, missing required participants, empty turns and stale validation prevent queueing. Editorial warnings remain distinguishable from parser errors.
- A two-speaker episode and a five-speaker panel generate one validated MP3; synthetic ordering tests and real listening tests provide separate evidence.
- Retry, rate limiting, quota/auth failures, partial audio, host loss and ambiguous hosted completion retain valid audio and produce accurate recovery states. Credentials and private text do not appear in exported diagnostics or provenance.
- Audition the same representative script on candidate providers: narration, questions, disagreement, humour, short reactions, technical names/numbers and long turns. Assess voice distinction, stability, intelligibility, pauses, natural delivery, generation time and actual usage cost. Test car-listening suitability separately.
- Additional providers pass this operational suite and listening review before being called supported. A new model or major provider behavior change receives renewed acceptance; marketing claims alone do not establish quality.

## Validation of this planning increment

The plan was checked against the current Core models, provider interfaces, local adapter, queue, desktop coupling and audio contract, and against the linked official provider documentation. Repository link targets and diff formatting are checked before commit. No runtime code is changed; no builds, executable tests, live hosted synthesis, charges, UI acceptance or listening comparison are claimed for this documentation increment.
