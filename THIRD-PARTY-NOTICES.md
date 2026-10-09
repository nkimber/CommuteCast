# Third-party components

This is the development licensing inventory, not a substitute for corporate redistribution approval. The portable app does not bundle FFmpeg or Docker Desktop. Speech containers include the inference components and models listed below. Preserve their notices when distributing images.

| Component | License / source | Role |
| --- | --- | --- |
| .NET / WPF | MIT; [dotnet/wpf](https://github.com/dotnet/wpf), [.NET](https://github.com/dotnet/runtime) | Windows desktop/runtime |
| Microsoft.Data.Sqlite 10.0.5 / Microsoft.Data.Sqlite.Core 10.0.5 | MIT declared by the exact NuGet package metadata; [EF Core](https://github.com/dotnet/efcore) | Durable queue adapter |
| SQLitePCLRaw 3.0.5 (`bundle_e_sqlite3`, `config.e_sqlite3`, `core`, `provider.e_sqlite3`) | Apache-2.0 declared by all four exact NuGet package metadata records; [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw/tree/ed046114d5a30534e13294d94d78eb73de896ad4) | SQLite native interop and configuration |
| SQLite | [Public domain](https://www.sqlite.org/copyright.html) | Local database |
| Serilog 4.3.0 / Serilog.Sinks.File 7.0.0 | Apache-2.0; [Serilog](https://github.com/serilog/serilog), [file sink](https://github.com/serilog/serilog-sinks-file) | Structured local rotating operational logs |
| Kokoro ONNX | MIT; [kokoro-onnx](https://github.com/thewh1teagle/kokoro-onnx) | Kokoro inference adapter |
| Kokoro 82M v1.0 | Apache-2.0; [model card](https://huggingface.co/hexgrad/Kokoro-82M) | Kokoro model/voices; ONNX conversion from pinned upstream release |
| Piper 1.3.0 | GPL-3.0; [Piper](https://github.com/OHF-Voice/piper1-gpl) | Separate local speech service. Distribution obligations require review. |
| Piper Lessac medium | [voice model card](https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/lessac/medium/MODEL_CARD) | Candidate English voice; dataset/model terms require review |

The installed English Piper catalog uses the pinned `rhasspy/piper-voices` revision `c10ece1aade47bb51c153c893d14e5bf8e5b7117`. Each voice has its own model card and dataset attribution; repository-level licensing does not replace those terms. The selected models are unmodified medium-quality single-speaker models. SHA256 identities and immutable artifact URLs are tracked separately from downloaded binaries.

| Additional Piper voice | Dataset attribution in the pinned model card |
| --- | --- |
| Amy, US English | [Amy model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_US/amy/medium/MODEL_CARD); points to MycroftAI/mimic3-voices for dataset terms |
| Bryce, US English | [Bryce model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_US/bryce/medium/MODEL_CARD); public-domain dataset recorded by the contributor |
| Joe, US English | [Joe model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_US/joe/medium/MODEL_CARD); CC0 dataset, OHF-Voice/voice-datasets |
| LJ Speech, US English | [LJ Speech model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_US/ljspeech/medium/MODEL_CARD); public-domain LJ Speech dataset, model contributed by Bryce Beattie |
| Alan, British English | [Alan model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_GB/alan/medium/MODEL_CARD); points to MycroftAI/mimic3-voices en_UK/apope_low for dataset terms |
| Alba, British English | [Alba model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_GB/alba/medium/MODEL_CARD); CC BY 4.0 dataset hosted by University of Edinburgh DataShare |
| Jenny, British English | [Jenny model card](https://huggingface.co/rhasspy/piper-voices/blob/c10ece1aade47bb51c153c893d14e5bf8e5b7117/en/en_GB/jenny_dioco/medium/MODEL_CARD); points to dioco-group/jenny-tts-dataset for dataset terms |
| ONNX Runtime | MIT; [onnxruntime](https://github.com/microsoft/onnxruntime) | CPU model execution |
| FastAPI / Uvicorn | MIT / BSD-3-Clause; [FastAPI](https://github.com/fastapi/fastapi), [Uvicorn](https://github.com/encode/uvicorn) | Local HTTP contract |
| NumPy / SoundFile | BSD-3-Clause; [NumPy](https://numpy.org/doc/stable/license.html), [SoundFile](https://github.com/bastibe/python-soundfile) | PCM generation |
| eSpeak NG / libsndfile | GPL-3.0 / LGPL-2.1; [eSpeak NG](https://github.com/espeak-ng/espeak-ng), [libsndfile](https://github.com/libsndfile/libsndfile) | Phonemization/audio service dependencies |
| FFmpeg / LAME | Build-dependent LGPL/GPL; [FFmpeg legal](https://ffmpeg.org/legal.html), [LAME](https://lame.sourceforge.io/) | External encoder/probe. The developer machine's FFmpeg is a GPL-enabled build. |
| Docker Desktop | [Subscription terms](https://www.docker.com/legal/docker-subscription-service-agreement/) | External prerequisite; corporate entitlement must be verified |

`services/speech/requirements.lock.txt` records resolved Python dependencies and `model-checksums.txt` records the exact model bytes tested. Consult upstream licenses for all transitive packages before redistributing a container. Candidate engine evaluation does not constitute legal or corporate policy approval.

Versioned desktop inventory observation (2026-10-07): verified hosted package build `8353e35` declares the two Microsoft.Data.Sqlite packages above, the four SQLitePCLRaw packages above and `SQLite/3.53.4` in `CommuteCast.Desktop.deps.json`. The installed SQLite package's `LICENSE.txt` states public domain. Both bundled runtime packs (`Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64`) are 10.0.12. These observations come from the actual downloaded dependency manifest and exact locally restored NuGet metadata, rather than a generic library license assumption. The earlier combined MIT classification for SQLitePCLRaw was incorrect and is replaced above. Package declarations are inventory evidence; complete notices/source materials and redistribution review remain release requirements.
