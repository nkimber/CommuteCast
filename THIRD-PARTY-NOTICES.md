# Third-party components

This is the development licensing inventory, not a substitute for corporate redistribution approval. The portable app does not bundle FFmpeg or Docker Desktop. Speech containers include the inference components and models listed below. Preserve their notices when distributing images.

| Component | License / source | Role |
| --- | --- | --- |
| .NET / WPF | MIT; [dotnet/wpf](https://github.com/dotnet/wpf), [.NET](https://github.com/dotnet/runtime) | Windows desktop/runtime |
| Microsoft.Data.Sqlite 10.0.5 / Microsoft.Data.Sqlite.Core 10.0.5 | MIT declared by the exact NuGet package metadata; [EF Core](https://github.com/dotnet/efcore) | Durable queue adapter |
| SQLitePCLRaw 3.0.5 (`bundle_e_sqlite3`, `config.e_sqlite3`, `core`, `provider.e_sqlite3`) | Apache-2.0 declared by all four exact NuGet package metadata records; [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw/tree/ed046114d5a30534e13294d94d78eb73de896ad4) | SQLite native interop and configuration |
| SQLite | [Public domain](https://www.sqlite.org/copyright.html) | Local database |
| Kokoro ONNX | MIT; [kokoro-onnx](https://github.com/thewh1teagle/kokoro-onnx) | Kokoro inference adapter |
| Kokoro 82M v1.0 | Apache-2.0; [model card](https://huggingface.co/hexgrad/Kokoro-82M) | Kokoro model/voices; ONNX conversion from pinned upstream release |
| Piper 1.3.0 | GPL-3.0; [Piper](https://github.com/OHF-Voice/piper1-gpl) | Separate local speech service. Distribution obligations require review. |
| Piper Lessac medium | [voice model card](https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/lessac/medium/MODEL_CARD) | Candidate English voice; dataset/model terms require review |
| ONNX Runtime | MIT; [onnxruntime](https://github.com/microsoft/onnxruntime) | CPU model execution |
| FastAPI / Uvicorn | MIT / BSD-3-Clause; [FastAPI](https://github.com/fastapi/fastapi), [Uvicorn](https://github.com/encode/uvicorn) | Local HTTP contract |
| NumPy / SoundFile | BSD-3-Clause; [NumPy](https://numpy.org/doc/stable/license.html), [SoundFile](https://github.com/bastibe/python-soundfile) | PCM generation |
| eSpeak NG / libsndfile | GPL-3.0 / LGPL-2.1; [eSpeak NG](https://github.com/espeak-ng/espeak-ng), [libsndfile](https://github.com/libsndfile/libsndfile) | Phonemization/audio service dependencies |
| FFmpeg / LAME | Build-dependent LGPL/GPL; [FFmpeg legal](https://ffmpeg.org/legal.html), [LAME](https://lame.sourceforge.io/) | External encoder/probe. The developer machine's FFmpeg is a GPL-enabled build. |
| Docker Desktop | [Subscription terms](https://www.docker.com/legal/docker-subscription-service-agreement/) | External prerequisite; corporate entitlement must be verified |

`services/speech/requirements.lock.txt` records resolved Python dependencies and `model-checksums.txt` records the exact model bytes tested. Consult upstream licenses for all transitive packages before redistributing a container. Candidate engine evaluation does not constitute legal or corporate policy approval.

Versioned desktop inventory observation (2026-10-07): verified hosted package build `8353e35` declares the two Microsoft.Data.Sqlite packages above, the four SQLitePCLRaw packages above and `SQLite/3.53.4` in `CommuteCast.Desktop.deps.json`. The installed SQLite package's `LICENSE.txt` states public domain. Both bundled runtime packs (`Microsoft.NETCore.App.Runtime.win-x64` and `Microsoft.WindowsDesktop.App.Runtime.win-x64`) are 10.0.12. These observations come from the actual downloaded dependency manifest and exact locally restored NuGet metadata, rather than a generic library license assumption. The earlier combined MIT classification for SQLitePCLRaw was incorrect and is replaced above. Package declarations are inventory evidence; complete notices/source materials and redistribution review remain release requirements.
