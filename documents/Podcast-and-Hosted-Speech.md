# Podcast and hosted speech

CommuteCast supports single-voice narration and two-to-five-speaker podcasts. Text generation stays in the user's chosen LLM. CommuteCast builds a prompt, validates pasted dialogue and generates the audio.

## Workflow

1. In **Build a prompt** or **New narration**, enable **Podcast conversation**. Open **Edit cast and podcast format**. Choose a format and **Apply format**, then edit the cast, roles, expertise and speaking personalities. Add/remove participants within the format's limits.
2. Build and copy a topic prompt. It carries the captured format and exact cast labels, asks for a natural fictional conversation, and excludes response preambles, document references, production notes and source appendices. Paste it into your preferred LLM yourself.
3. Paste the response into the editor. Each nonblank line must be `Alex: spoken dialogue`, using exact configured names. Each cast member must speak. No unlabelled continuation lines, URLs, citations, Markdown, vocal tags or stage directions. Validation rejects incompatible content with line locations; it cannot prove factual accuracy or spoken fidelity.
4. Choose the speech provider and model. Assign each participant an available provider voice, supported pace and delivery controls. Refresh voices and audition participants. **Save personality** saves expertise/style plus a separate voice binding for the current provider/model. Queued episodes retain independent snapshots when libraries change.
5. **Validate podcast dialogue** reports turns, render blocks, word count, approximate listening length and speaker balance. Shared voices produce a recognizability reminder. Changes to text, cast, provider or speech settings invalidate validation.
6. Review spoken text, then **Create MP3**. Speaker labels are removed before TTS. The queue generates missing blocks, normalizes audio and publishes one validated MP3. **Use as a new draft** restores a saved episode's cast and model for revision.

Disable Podcast conversation to paste ordinary prose or build narration-only prompts. Existing local narration jobs keep their original fingerprints and audio policy.

## Providers and credentials

In **Settings → Speech providers**, select a provider and model, save its API key, then check the account and refresh voices. Save narration defaults explicitly to select that provider automatically on relaunch. Hosted keys live in per-user Windows Credential Manager (`CommuteCast/TTS/<provider>`), outside settings, drafts, backups and Git. A restore onto another Windows profile needs keys to be entered again. The password field is cleared after saving.

| Provider | Rendering and controls |
| --- | --- |
| Kokoro / Piper | Installed local voices, individual turns, pace, saved delivery recipes, Kokoro blends and Piper variation. Requires updated Docker provisioning. See [local voice quality](Local-Voice-Quality.md). |
| OpenAI | Individual turns; mini-TTS models support separate delivery instructions. Legacy `tts-1`/`tts-1-hd` use voice and pace. |
| ElevenLabs | V3/V4 can render adjacent dialogue turns with individual voice IDs and controlled emotion tags. Other listed models render individual turns. Pace is limited to 0.7–1.2. |
| Cartesia | Individual turns using Sonic, voice ID, pace and supported emotion controls. Emotion guidance is beta. |
| Google Gemini | Two-speaker dialogue blocks or individual turns for larger casts. Delivery instructions control pace/style; the numeric pace must be 1.0. Uses Gemini 3.8 TTS through the Interactions API. |

Joint blocks require **Use joint dialogue blocks**, a compatible model/cast and pace 1.0 for every participant. Otherwise the same provider renders individual segments. Joint blocks are limited to 1,800 prepared text characters, including controlled ElevenLabs tags, and 16 turns; individual segments to 450, or 900 for new local profiles with natural phrasing. Long turns split at semantic boundaries where possible. Each block retains ordered text and speaker assignments. A joint block is regenerated as a whole when missing or invalid. At small window sizes the podcast editor scrolls vertically, preserving room for pasted dialogue and access to validation and Create MP3.

Provider contracts were checked against [OpenAI speech documentation](https://developers.openai.com/api/docs/guides/text-to-speech), [ElevenLabs dialogue API](https://elevenlabs.io/docs/api-reference/text-to-dialogue/convert), [Cartesia bytes API](https://docs.cartesia.ai/api-reference/tts/bytes), and [Gemini speech generation](https://ai.google.dev/gemini-api/docs/speech-generation) on October 10, 2026. Hosted model aliases and voices can change remotely; captured requested model IDs do not promise immutable weights or identical regenerated audio. No custom voice cloning, cross-provider casts, overlaps or direct LLM calls are implemented. Qwen remains an optional local-engine evaluation candidate, rather than an installed dependency.

## Audio, cost and recovery

Podcast units are decoded to mono 24 kHz, signed 16-bit PCM. Hosted and legacy local episodes normalize each unit to a -19 LUFS / -2 dB true-peak target; speaker changes get an 80 ms join and same-speaker fragments get no added gap. New natural local profiles retain native dynamics, top up existing boundary silence to the captured pause, then measure and normalize the assembled episode before encoding. The default local speaker pause is 140 ms. Timing inside jointly generated dialogue stays with the provider. All units are hash-validated, assembled in order, encoded once to 128 kbps MP3 and fully decoded/probed before export. Metadata records provider/model, participant voice IDs, local recipes and audio policy, without script text, API keys, dictionary content or delivery instructions. Target loudness is processing guidance, not a listening-quality guarantee.

Hosted speech sends spoken text to the selected provider and may be billed, including auditions and retries. There is no automatic switch from local to cloud. Effective character rates are optional, manually recorded by model with date/currency. Blank or mismatched-model rates mean **cost unavailable**; token-based and subscription billing may differ from an effective character estimate. Provider-reported usage is retained only when returned in supported fields; missing billing counts are not inferred as zero.

Before posting inference, the app saves a source-free request attempt. HTTP rejection, received audio and uncertain transport/cancellation states remain distinguishable. Requests are never automatically repeated: rate/quota errors require explicit retry and honor a recorded provider backoff. After host loss, sleep or shutdown, an unresolved request without a validated audio receipt stops for provider-usage inspection and explicit **Retry / resume**. Validated units are reused. A local cancellation does not prove remote cancellation. Hosted audition request history remains after private preview cleanup and is available in Settings; saved-job request IDs/states appear in its details.

Schema 5 added hosted audition history and podcast/hosted contracts; schema 6 additionally fences local voice recipes and speaker pronunciation overrides. Migration backs up recognized older databases. Podcast drafts add a fourth field and preserve incomplete cast edits; older builds refuse the extended format. Nonsecret configuration and episode snapshots travel through existing backups. API keys do not.

## Validation limits

Automated tests exercise provider request shapes, credentials separation, bounded audio responses, uncertain requests, dialogue validation, model-specific rendering, legacy records, migration/backup, native WPF bindings, synthetic two/five-speaker full MP3 generation and retained-segment retry. These establish implementation behavior. Live hosted synthesis, account/model permissions, provider billing reconciliation, expressive speech quality, car listening and phone delivery require separate acceptance with configured accounts and listeners. No live hosted calls or listening approval are claimed for this implementation.
