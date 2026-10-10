# Local voice quality

CommuteCast captures a complete local voice recipe with each narration or podcast. Saved defaults, named narration presets and speaker personality bindings retain the same choices for future work. Jobs keep independent copies; changing a library entry never changes a queued episode or its validated segments.

## Controls

In **New narration → Voice & pace → Local voice delivery**, enable **Natural phrasing and volume**. New installations and the starter presets enable it by default. An existing saved default or preset without a local recipe keeps its previous behavior until you enable it and save your choices.

| Control | Purpose |
| --- | --- |
| Natural phrasing and volume | Groups complete sentences into blocks up to 900 characters, preserves native pauses and dynamics, tops up joins only when needed, and matches final listening volume. |
| Kokoro second voice and proportion | Uses a fixed weighted blend of two installed voices with the same US or British English accent. Start with a small proportion, audition, then save the preferred recipe. |
| Piper sound/timing variation | Uses the installed model's defaults while unchecked. Checking it exposes Piper's native `noise_scale` and `noise_w_scale`. Lower values reduce variation; excessive changes can make delivery flat or unstable. |
| Sentence/paragraph pause | Minimum desired pauses at generated block boundaries; pauses inside a model-generated block stay with that model. Defaults: 220/500 ms. |
| Input level | Attenuates the model output before final volume matching; it is not a final MP3 volume control. In podcasts, per-speaker attenuation balances relative levels. |
| Expressive sample | A repeatable passage containing questions, contrast and a paragraph break. It captures the current voice, pace, dictionary and local recipe. |

For podcasts, open **Edit cast and podcast format**, expand each **Local voice profile**, and choose its blend or variation, input level and pause before the next speaker. The default speaker-change pause is 140 ms. Add pronunciation overrides as `term=spoken words`, one per line. Speaker rules replace matching global terms; other global rules remain. **Save personality** saves the provider voice, pace, local recipe and pronunciation overrides together. Natural phrasing and final volume follow the narration-wide setting.

Auditions preserve native output levels and stay private. The final MP3 gets volume matching. Listen to the complete episode when judging speaker balance. Personality and expertise guide the writing prompt; Kokoro/Piper do not interpret free-form emotional delivery instructions. Blends and variation are useful tuning tools, but they do not establish voice cloning or actor-like control.

## Audio and compatibility

Natural narration uses `natural900-v2` / `pcm24k-natural-loudnorm-v2`; local podcasts use `podcast-natural900-v2` / `podcast-pcm24k-natural-loudnorm-v2`. Existing jobs retain their older contract, fingerprint, chunk manifest and fixed gaps. New recipes participate in fingerprints and duration-history matching. Retry uses the captured per-speaker recipe and reuses valid receipts.

Kokoro retains untrimmed native pauses. Piper disables its per-utterance amplitude normalization in natural mode. CommuteCast resamples each segment to mono 24 kHz PCM, measures existing edge silence in 10 ms windows, and only adds the missing pause. It never removes samples, breaths or longer native pauses. Sentence abbreviations and Unicode pairs are protected during splitting; an unavoidable mid-sentence split gets no extra gap.

After assembling the complete ordered episode, FFmpeg measures loudness and applies a second pass targeting −19 LUFS, −2 dB true peak and an 11 LU loudness range, then encodes MP3 once. Very short utterances below the LUFS measurement window use static RMS gain bounded by the peak target. The measured final MP3 can differ slightly from the target after resampling/encoding. No per-speaker volume normalization is applied to natural episodes, preserving relative levels and expressiveness.

The speech health contract advertises `localVoiceContract: 1`. An older service or a missing blend voice is refused before reservation or sending text. Updating the adapter changes its fingerprint and image identity. Finished older MP3s remain usable; unfinished jobs need their captured compatible image or **Use as a new draft**. Queue schema 6 prevents older builds from ignoring recipes or speaker dictionaries. Migration retains exact legacy payloads and makes a verified original SQLite snapshot. Older application/package versions refuse schema 6; use the compatible backup/rollback workflow when downgrading.

To update a separate installation, close the app after current work settles, then provision and relaunch:

```powershell
.\scripts\Provision-Speech.ps1 -Build
.\scripts\Start-CommuteCast.ps1
```

## Repeatable Kokoro/Piper comparison

Build a separate image and use isolated loopback services so existing narration pins stay intact. The harness requires a new output directory and verifies the local recipe contract. It generates baseline, natural, natural-repeat and tuned samples; `--long` adds eight alternating speaker turns. The same text and voice remain fixed for the short comparisons. Tuning uses Heart + 25% Bella for Kokoro and Lessac with noise 0.55/0.65 for Piper.

```powershell
$modelCache = Join-Path $env:LOCALAPPDATA 'CommuteCast\provisioning-models'
docker build --build-context "commutecast_models=$modelCache" -t commutecast-speech:voice-quality services/speech
docker run -d --name commutecast-quality-kokoro --cpus 2 --memory 2g --security-opt no-new-privileges:true -p 127.0.0.1:18765:8765 -e COMMUTECAST_ENGINE=kokoro commutecast-speech:voice-quality
docker run -d --name commutecast-quality-piper --cpus 2 --memory 1g --security-opt no-new-privileges:true -p 127.0.0.1:18766:8765 -e COMMUTECAST_ENGINE=piper commutecast-speech:voice-quality
dotnet run --project tools/CommuteCast.VoiceQuality -- kokoro http://127.0.0.1:18765 artifacts/quality-kokoro --long
dotnet run --project tools/CommuteCast.VoiceQuality -- piper http://127.0.0.1:18766 artifacts/quality-piper --long
docker rm -f commutecast-quality-kokoro commutecast-quality-piper
```

`comparison.json` records model fingerprint, recipe, duration, synthesis time, real-time factor, audio hashes and final MP3 loudness. Generated audio/workspaces remain ignored. Run one comparison at a time for less variable timing, and use a separate build configuration if another harness process is still using its binaries.

October 10, 2026 trials used the pinned ONNX weights, CPU services limited to two CPUs, and image `sha256:3b70440b9b0c10dedb9ea6a1afb32fcaa82170f09a1a264fd0c7ff6fe10c60c1`. All ten files passed ordered-segment/duration, MP3 format and full-decode validation. These are observed audio measurements, not a listener's expressiveness score:

| Engine / variant | Duration | Final LUFS | Final true peak |
| --- | ---: | ---: | ---: |
| Kokoro baseline | 23.81 s | −22.75 | −4.65 dBTP |
| Kokoro natural | 26.05 s | −19.58 | −2.43 dBTP |
| Kokoro fixed blend | 26.32 s | −19.57 | −2.46 dBTP |
| Kokoro two speakers, 536 words | 205.10 s | −19.55 | −2.37 dBTP |
| Piper baseline | 22.96 s | −16.27 | −0.39 dBTP |
| Piper natural | 22.97 s | −19.44 | −2.60 dBTP |
| Piper tuned variation | 22.06 s | −19.45 | −2.86 dBTP |
| Piper two speakers, 536 words | 201.84 s | −19.50 | −2.43 dBTP |

Kokoro's natural and natural-repeat MP3s decoded to the same PCM SHA256 (`4b80b6f10e90f37031fef823a5d9a23cdbdb979e233f796d5afb933a05282e01`). Piper's repeats were 22.97 and 23.04 seconds, reflecting stochastic synthesis. Timing was measured on a machine also running regression tests and other services; the eight-turn real-time factors were 1.73 for Kokoro and 0.39 for Piper. This does not establish general throughput or identical delivery across arbitrary texts. Listening approval remains pending.

## Optional expressive-model experiments

`tools/CommuteCast.VoiceQuality/compare_expressive.py` is a separate CPU experiment, outside the application provider list. It requires predownloaded local models, uses offline Hugging Face loading, fixes speaker/reference and generation settings, and writes neutral, expressive and expressive-repeat float WAVs plus hashes, load/synthesis timing, package versions and model inventory. The fixed seed initializes the run once; repeats intentionally let sampling continue to expose variation. It does not add dependencies or automatic downloads to CommuteCast.

Use separate Python 3.11 environments for the candidates. Source interfaces were checked at Qwen commit `022e286b98fbec7e1e916cb940cdf532cd9f488e` and Chatterbox commit `5de7a54aa4e5e2baadb0182dde554908b48b85c2`. Install these explicit revisions in those environments, then download complete model snapshots and their required tokenizer assets following the upstream instructions. Keep environments and weights under ignored `artifacts` or outside the repository.

```powershell
# In the Qwen environment, after installing the pinned source and local model snapshot:
python tools/CommuteCast.VoiceQuality/compare_expressive.py qwen C:/models/Qwen3-TTS-12Hz-1.7B-CustomVoice artifacts/qwen-quality --speaker Ryan
# In the Chatterbox environment, with a clean local mono reference longer than five seconds:
python tools/CommuteCast.VoiceQuality/compare_expressive.py chatterbox-nano C:/models/chatterbox-nano artifacts/nano-quality --reference C:/samples/reference.wav
```

The Qwen trial specifically requires **1.7B CustomVoice** with its fixed preset speaker and instruction control. The 0.6B CustomVoice implementation ignores instructions; Base is a different cloning interface. See [Qwen's implementation](https://github.com/QwenLM/Qwen3-TTS/blob/022e286b98fbec7e1e916cb940cdf532cd9f488e/qwen_tts/inference/qwen3_tts_model.py).

Nano caches the same reference conditioning for all samples and uses a `[chuckle]` tag in the expressive variant. Its Turbo/Nano interface does not support the original Chatterbox CFG/exaggeration controls. See [Nano's example](https://github.com/resemble-ai/chatterbox/blob/5de7a54aa4e5e2baadb0182dde554908b48b85c2/example_tts_nano.py) and [native controls](https://github.com/resemble-ai/chatterbox/blob/5de7a54aa4e5e2baadb0182dde554908b48b85c2/src/chatterbox/tts_turbo.py). These experiments use CPU, with no assumption of NVIDIA CUDA support on the Intel Arc laptop.

Candidate adapter contract tests pass without downloading weights. Actual Qwen/Nano inference, memory/latency, voice identity and listening acceptance remain unexecuted. Before promoting a candidate into the app, compare volume-matched clips and a complete episode, transcribe/check for omissions and invented speech, judge speaker recognizability and phrasing, measure resource use, and require the app's existing offline provisioning/admission/recovery contracts. Prefer listening scores for consistency, naturalness and expression over audio hashes as a quality decision.
