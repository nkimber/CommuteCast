# Local TTS options for future exploration

**Reviewed:** November 2, 2026

**Status:** Source-based research and proposed experiments; no new provider selected or integrated.

This records the local speech options reviewed for CommuteCast, including an optional Nemotron script-generation stage. Capabilities and upstream performance claims are research inputs. Candidate inference, local memory/latency measurements, listening comparisons and application acceptance remain unexecuted. Recheck the linked sources and pin exact model/runtime revisions when starting an experiment.

The installed Kokoro and Piper services remain the comparison baselines. Their existing delivery controls, measured audio results and experimental Qwen/Chatterbox harness are documented in [Local voice quality](Local-Voice-Quality.md). The [multi-provider plan](Multi-Provider-TTS-and-Podcast-Plan.md) records the existing separation between writing a script and synthesizing it.

## Hardware and intended use

The development laptop inspected during this review has an Intel Core Ultra 7 265H, Intel Arc 140T GPU and approximately 64 GB system RAM. Start with CPU inference and record the actual backend used. NVIDIA CUDA and Apple Silicon results do not establish performance on this machine. Any Intel GPU acceleration needs a separate runtime compatibility and performance trial.

The Windows micro PC described in [Web application setup](Web-Application.md) is a separate deployment target. Inventory and benchmark that machine before treating laptop results as deployment evidence.

The main use cases are sustained narration and podcasts with two to five distinguishable speakers. Prioritize pleasant long listening, accurate spoken text, consistent voice identity and manageable generation time. Streaming latency is useful for auditions, but complete-episode reliability also matters.

## Candidate overview

| Option | Model scale | Purpose of an experiment | Main qualification |
| --- | --- | --- | --- |
| Pocket TTS | 100M; English variants approximately 18M, 7M and 2M | Lightweight CPU narration and reference voices | Check capabilities and quality separately for each variant. |
| Chatterbox Nano | 110M | Expressive English narration/dialogue with reference voices | Specific vocal-event tags differ from unrestricted delivery instructions. |
| Magpie Multilingual with NeMo-Speech.cpp | Approximately 0.36B, plus codec | Native local NVIDIA speech stack on Windows/CPU | Benchmark the complete runtime and codec, not parameter count alone. |
| Kitten-TTS 2 | 1.7B, quantized | Richer expression and voice cloning on CPU | C++ runtime is beta; weights have a separate community license. |
| KittenTTS 0.8 | 15M, 40M and 80M | Compact ONNX alternatives to Kokoro/Piper | Features differ from Kitten-TTS 2. |
| Qwen3-TTS | 0.6B and 1.7B | Preset voices, cloning or instructed delivery according to checkpoint | 0.6B CustomVoice ignores delivery instructions. |
| Supertonic 3 | Approximately 99M | Archived ONNX multilingual/expression option | Official development and support have ended. |
| Nemotron 3.5 Bolt E4B | 8B total / 4B effective; compact quantized edition | Optional local script generation | Text output; requires a separate TTS model for audio. |

Parameter counts and weight downloads are not runtime RAM requirements. Sources and variant-specific constraints follow.

## Pocket TTS

Kyutai's CPU-focused Pocket TTS provides streaming and voice cloning. The project lists seven supported languages for the family. Its October 2026 English releases include 18.2M, 6.9M and 1.9M variants, with decoder included in those counts. Start with the 100M model and the 18M English variant, then explore smaller versions if the quality/resource tradeoff is useful. Do not assume every small variant inherits all larger-model capabilities.

Upstream speed claims use other hardware and need local measurement. Evaluate pronunciation, voice identity across chunks and sustained narration before selecting a default. The project also links separate voice licensing information; preserve the identity and provenance of any chosen preset/reference.

Sources: [official repository and model selection](https://github.com/kyutai-labs/pocket-tts), [model card](https://huggingface.co/kyutai/pocket-tts).

## Chatterbox Nano and related variants

Chatterbox Nano is the 110M English model in Resemble AI's family. It supports CPU inference, reference-based voices and vocal-event tags such as `[laugh]` and `[chuckle]`. Upstream reports approximately three times real time on eight CPU cores; that is not a CommuteCast laptop measurement.

Nano uses the Turbo interface and single-step decoder. It does not expose the original Chatterbox model's CFG/exaggeration controls. Model-specific markup must be interpreted by a compatible adapter so control text does not become unintended speech.

Related options include English Turbo at 350M and Multilingual V3 at 500M. Keep them as follow-up comparisons if Nano's language coverage or delivery is insufficient. The existing experiment covers Nano only, with a fixed reference and neutral/expressive/repeat samples.

Sources: [official model lineup and examples](https://github.com/resemble-ai/chatterbox), [existing experiment notes](Local-Voice-Quality.md#optional-expressive-model-experiments).

## NVIDIA Magpie Multilingual with NeMo-Speech.cpp

Magpie is the actual TTS component in NVIDIA's cascaded voice-agent architecture. The model is named `magpie_tts_multilingual_357m`; the current card describes approximately 364M parameters and twelve languages. Its stack also needs the companion NanoCodec decoder and tokenizer assets.

NeMo-Speech.cpp provides a native C++ path, Windows installation, CPU inference and local HTTP speech routes. This makes Magpie worth testing independently of an NVIDIA GPU or a local language model. Upstream reports faster-than-real-time CPU synthesis; reproduce useful throughput on the intended host before relying on it.

Evaluate preset voice suitability, complete-stack RAM, initialization time and the exact HTTP/audio contract. The weights use the NVIDIA Open Model License; runtime code and model terms are separate.

Sources: [Magpie model card](https://huggingface.co/nvidia/magpie_tts_multilingual_357m), [NeMo-Speech.cpp runtime](https://github.com/NVIDIA/NeMo-Speech.cpp), [benchmark methodology](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/main/BENCHMARK.md).

## Kitten-TTS 2 and KittenTTS 0.8

Kitten-TTS 2 is a quantized 1.7B speech model with 115 built-in voices, reference cloning and beta emotion/event/emphasis controls. Its default weights are approximately 954 MB; the 492 MB `emb4` option quantizes embeddings with a quality tradeoff. These sizes exclude the complete runtime: upstream lists about 6 GB CPU RAM and additional decoder assets. The optimized C++ implementation is beta. Test short chunks and joins because upstream identifies repetition/truncation risks with a whole script in one pass.

The older 0.8 ONNX family offers 80M Mini, 40M Micro and 15M Nano models, including a 25 MB INT8 Nano download. These are separate compact baselines; do not transfer Kitten-TTS 2's capabilities to them.

The repository's Apache license covers code. Kitten-TTS 2 weights use the Stellon Labs Community License, with commercial registration and revenue/funding conditions. Resolve suitability for the intended use and distribution before promoting this option. Check each 0.8 model's own terms independently.

Sources: [official lineup, runtime and expression notes](https://github.com/KittenML/KittenTTS), [Kitten-TTS 2 weight license](https://huggingface.co/KittenML/kitten-tts-2/blob/main/LICENSE.md).

## Qwen3-TTS

Checkpoint selection determines the experiment:

| Checkpoint family | Experiment |
| --- | --- |
| 0.6B CustomVoice | Smaller preset-voice option; delivery instructions are disabled by the current implementation. |
| 1.7B CustomVoice | Preset voices with instruction-based delivery control; the existing expressive harness targets this checkpoint. |
| 0.6B / 1.7B Base | Reference-based cloning through the separate cloning API. |
| 1.7B VoiceDesign | Description-based voice design; requires a separate experiment from CustomVoice. |

The family supports ten languages. CPU memory, synthesis time and long-form consistency remain open questions on the target machines. The current experiment's 1.7B requirement is intentional: replacing it with 0.6B would remove the instructed-expression comparison. Recheck source behavior when pinning a newer runtime.

Sources: [official checkpoint lineup](https://github.com/QwenLM/Qwen3-TTS), [instruction handling in the implementation](https://github.com/QwenLM/Qwen3-TTS/blob/main/qwen_tts/inference/qwen3_tts_model.py), [existing experiment instructions](Local-Voice-Quality.md#optional-expressive-model-experiments).

## Supertonic 3

Supertonic 3 is an approximately 99M ONNX model with 31 languages and ten expression tags. Its repository includes C# and other runtime examples, making it technically relevant to a Windows/.NET application.

The official repository was archived on September 9, 2026; development and support ended. Models and code remain available through the archive, with separate licenses. Keep this as a lower-priority experiment that would require accepting maintenance responsibility. Download from the archive namespace and pin its documented revision; historical package defaults can point at the previous namespace.

Source: [official Supertonic archive and model instructions](https://github.com/supertone-oss-archive/supertonic).

## Nemotron 3.5 Bolt E4B as an optional writing stage

Bolt E4B accepts and produces text. NVIDIA describes 8B total / 4B effective parameters and publishes a compact quantized edition targeting approximately 4 GB in its intended workloads. Context, caches and runtime overhead still affect total memory. NVIDIA's voice-agent blueprint pairs its LLM with a separate TTS component such as Magpie.

A possible fully local workflow is:

```text
Source material
    -> Bolt E4B: draft narration or speaker-labelled dialogue
    -> Review and validate the script
    -> Pocket / Chatterbox / Magpie / another TTS: render each speaker
    -> CommuteCast: assemble, match loudness, validate and export MP3
```

This is a proposed workflow. Existing CommuteCast writing remains a prompt-copy/LLM/paste process. A supplied script can go directly to speech. Explore Bolt separately from speech-provider work so writing quality and audio quality can be assessed independently. Verify model/runtime compatibility on Intel hardware; NVIDIA deployment examples are not an Intel Arc acceptance result. Its conversational mode favors concise replies, so validate requested episode length before using that mode for long scripts.

Sources: [Bolt E4B model card](https://huggingface.co/nvidia/NVIDIA-Nemotron-3.5-Bolt-E4B-4GB/blob/main/README.md), [NVIDIA introduction](https://research.nvidia.com/labs/nemotron/NVIDIA-Nemotron-3.5-Bolt-E4B-4GB/), [voice-agent component architecture](https://github.com/NVIDIA-AI-Blueprints/nemotron-voice-agent).

## Proposed exploration order

1. Compare Pocket 100M and 18M, Chatterbox Nano and Magpie against the installed Kokoro/Piper baselines. These are candidate priorities, not a listening-quality ranking.
2. Evaluate Kitten-TTS 2 if its weight terms fit the intended use; compare 0.8 separately for compact ONNX operation.
3. Run the existing Qwen 1.7B CustomVoice experiment for instructed expression. Evaluate 0.6B, Base and VoiceDesign only against their respective goals.
4. Explore Bolt if local writing is desired. A successful speech trial does not depend on this stage.
5. Retain Supertonic as an archived alternative with lower integration priority.

## Repeatable experiment and promotion criteria

Use isolated environments/services and predownloaded, pinned models. Keep the current application services, model pins and queued jobs intact. Store generated audio, weights and environments outside Git, using ignored experiment folders where appropriate.

Use one fixed short narration containing questions, paragraph transitions, names, abbreviations, dates and numbers. Add a two-speaker script and a complete five-to-ten-minute episode. Keep speaker/reference recordings and settings fixed, record any engine-specific normalization or markup, and generate repeats. Compare clips at matched listening volume and audition the full episode in the intended listening environment.

Record model/runtime revisions, licenses, backend, thread limits, hardware, cold-load time, synthesis time, peak process/system RAM and audio duration. Define real-time factor as synthesis seconds / audio seconds; lower is faster. Track initialization separately, and record end-to-end export time as well. Transcribe and manually check for omissions, extra speech, repetition and spoken control tags. Capture listening notes for naturalness, pronunciation, speaker recognizability, joins and fatigue.

Promoting a model needs a compatible provider adapter, offline provisioning and identity checks, honest capability flags, immutable per-job/per-speaker settings, supported chunking, pause/stop/retry recovery, admission control, audio conversion and final MP3 validation. Exercise those contracts in both relevant clients before reporting application acceptance. An upstream demo, a valid WAV or a passing adapter contract test establishes only its own scope.

**Current evidence:** The existing notes record prior Kokoro/Piper audio trials and synthetic Qwen/Nano adapter checks. This research adds no candidate inference, downloads, benchmarks, listener approval, runtime changes or deployment acceptance. No winner has been selected.
