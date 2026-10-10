"""CommuteCast contract v1. No text logs, network inference, or runtime downloads."""
import hashlib
import io
import json
import logging
import os
import threading
import wave
import uuid
import time
from diagnostics import emit, failure
from contextlib import asynccontextmanager
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel, Field, ConfigDict

ENGINE = os.environ.get("COMMUTECAST_ENGINE", "kokoro")
state = "loading"
model = None
voices = []
fingerprint = ""
active = 0
execution = None
gate = threading.Lock()
instance = uuid.uuid4().hex
sequence = 0
retired = 0
reserved = False
logging.getLogger("kokoro_onnx").setLevel(logging.CRITICAL)
logging.getLogger("phonemizer").setLevel(logging.CRITICAL)


def cpu_session(path):
    # Match the verified two-CPU service quota; default pools use host physical cores.
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    return ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])


def load():
    global model, voices, fingerprint, state, execution
    try:
        folder = Path("/models") / ENGINE
        h = hashlib.sha256()
        h.update(Path(__file__).read_bytes())
        h.update(Path("/app/diagnostics.py").read_bytes())
        h.update(Path("/app/voice_library.py").read_bytes())
        h.update(Path("/app/requirements.txt").read_bytes())
        h.update(Path("/app/requirements.lock.txt").read_bytes())
        for file in sorted(folder.iterdir()):
            if file.is_file():
                h.update(file.name.encode())
                with file.open("rb") as stream:
                    for block in iter(lambda: stream.read(1024 * 1024), b""):
                        h.update(block)
        fingerprint = ENGINE + ":contract-v1:" + h.hexdigest()
        if ENGINE == "kokoro":
            from kokoro_onnx import Kokoro
            session = cpu_session(folder / "kokoro-v1.0.onnx")
            model = Kokoro.from_session(session, str(folder / "voices-v1.0.bin"))
            voices = sorted(v for v in model.get_voices() if v.startswith(("af_", "am_", "bf_", "bm_")))
        elif ENGINE == "piper":
            from voice_library import PiperVoiceLibrary
            model = PiperVoiceLibrary(folder, cpu_session)
            voices = model.voice_ids
            default_voice = "en_US-lessac-medium" if "en_US-lessac-medium" in voices else voices[0]
            session = model.select(default_voice).session
        else:
            raise ValueError("unsupported engine")
        actual = session.get_session_options()
        execution = {"provider": session.get_providers()[0], "intraOpThreads": actual.intra_op_num_threads,
                     "interOpThreads": actual.inter_op_num_threads, "mode": actual.execution_mode.name,
                     "intraOpSpinning": actual.get_session_config_entry("session.intra_op.allow_spinning"),
                     "interOpSpinning": actual.get_session_config_entry("session.inter_op.allow_spinning")}
        state = "ready"
        emit("model_ready", engine=ENGINE, instance=instance)
    except Exception as error:
        failure("model_load_failed", error, engine=ENGINE, instance=instance)
        state = "failed"


@asynccontextmanager
async def lifespan(app):
    threading.Thread(target=load, daemon=True).start()
    yield


app = FastAPI(title="CommuteCast Speech", version="1", lifespan=lifespan)


@app.get("/health")
def health():
    with gate:
        return {"service": "CommuteCast", "contract": 1, "engine": ENGINE,
                "fingerprint": fingerprint, "voices": voices, "state": state, "active": active, "execution": execution,
                "admission": 1, "instance": instance, "sequence": sequence, "localVoiceContract": 1}


class AdmissionRequest(BaseModel):
    instance: str = Field(pattern=r"^[a-f0-9]{32}$")
    sequence: int = Field(ge=1, le=9007199254740991)
    fingerprint: str


def admission_identity(request):
    if request.instance != instance or request.fingerprint != fingerprint:
        raise HTTPException(409, "Speech process or model identity changed")


def admission_result(request, phase):
    return {"service": "CommuteCast", "contract": 1, "engine": ENGINE,
            "instance": instance, "sequence": request.sequence, "state": phase}


@app.post("/reserve")
def reserve(request: AdmissionRequest):
    global sequence, reserved
    with gate:
        admission_identity(request)
        if state != "ready":
            raise HTTPException(503, "Model is not ready")
        if active or reserved:
            raise HTTPException(429, "An inference reservation is active")
        if request.sequence != sequence + 1:
            raise HTTPException(409, "Inference reservation is stale")
        sequence = request.sequence
        reserved = True
        return admission_result(request, "reserved")


@app.post("/settle")
def settle(request: AdmissionRequest):
    global sequence, retired, reserved
    with gate:
        admission_identity(request)
        if request.sequence > sequence + 1:
            raise HTTPException(409, "Inference reservation is incompatible")
        # Retire even a reservation whose HTTP request has not arrived yet. This
        # high-water mark permanently rejects delayed reserve/speech callbacks.
        retired = max(retired, request.sequence)
        sequence = max(sequence, request.sequence)
        if not active and request.sequence >= sequence:
            reserved = False
        return admission_result(request, "active" if active else "settled")


class LocalVoiceOptions(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    naturalPhrasing: bool = True
    blendVoice: str = Field(default="", max_length=80)
    blendWeight: float = Field(default=.25, ge=0, le=1)
    noiseScale: float | None = Field(default=None, ge=0, le=1.5)
    noiseWidth: float | None = Field(default=None, ge=0, le=1.5)
    gainDb: float = Field(default=0, ge=-12, le=0)
    sentencePauseMs: int = Field(default=220, ge=0, le=1200)
    paragraphPauseMs: int = Field(default=500, ge=0, le=2000)
    turnPauseMs: int = Field(default=140, ge=0, le=1200)
    version: int = Field(default=1, ge=1, le=1)

    def validate_engine(self, engine, voice, installed):
        if engine == "piper" and self.blendVoice or engine == "kokoro" and (self.noiseScale is not None or self.noiseWidth is not None):
            raise HTTPException(409, "Local voice controls do not match the engine")
        if self.blendVoice and (self.blendVoice not in installed or self.blendVoice == voice or self.blendVoice[0] != voice[0]):
            raise HTTPException(409, "Choose two installed Kokoro voices with the same accent")


class SpeechRequest(AdmissionRequest):
    text: str = Field(min_length=1, max_length=900)
    voice: str
    speed: float = Field(ge=0.7, le=1.4)
    fingerprint: str
    localVoice: LocalVoiceOptions | None = None


@app.post("/speech")
def speech(request: SpeechRequest):
    global active, retired, reserved
    with gate:
        admission_identity(request)
        if state != "ready":
            raise HTTPException(503, "Model is not ready")
        if request.voice not in voices:
            raise HTTPException(409, "Voice or model identity changed")
        if request.localVoice is not None:
            request.localVoice.validate_engine(ENGINE, request.voice, voices)
        if active:
            raise HTTPException(429, "Inference is active; wait for quiescence")
        if not reserved or request.sequence != sequence or request.sequence <= retired:
            raise HTTPException(409, "Inference reservation is retired or missing")
        active = 1
    started = time.monotonic()
    emit("synthesis_started", engine=ENGINE, instance=instance, sequence=request.sequence)
    try:
        output = io.BytesIO()
        local = request.localVoice
        gain = 10 ** (local.gainDb / 20) if local else 1.0
        if ENGINE == "kokoro":
            # create() splits long phoneme sequences internally; the desktop sends small chunks.
            language = "en-gb" if request.voice.startswith("b") else "en-us"
            voice = request.voice
            if local and local.blendVoice:
                voice = (1 - local.blendWeight) * model.get_voice_style(request.voice) + local.blendWeight * model.get_voice_style(local.blendVoice)
            options = {"trim": False} if local and local.naturalPhrasing else {}
            samples, rate = model.create(request.text, voice=voice, speed=request.speed, lang=language, **options)
            sf.write(output, np.asarray(samples) * gain, rate, format="WAV", subtype="PCM_16")
        else:
            from piper import SynthesisConfig
            with wave.open(output, "wb") as wav:
                options = {} if local is None else {"noise_scale": local.noiseScale, "noise_w_scale": local.noiseWidth,
                    "normalize_audio": not local.naturalPhrasing, "volume": gain}
                model.select(request.voice).synthesize_wav(request.text, wav, syn_config=SynthesisConfig(length_scale=1 / request.speed, **options))
        emit("synthesis_completed", engine=ENGINE, instance=instance, sequence=request.sequence,
             elapsed_ms=round((time.monotonic() - started) * 1000), audio_bytes=output.getbuffer().nbytes)
        return Response(output.getvalue(), media_type="audio/wav")
    except Exception as error:
        failure("synthesis_failed", error, engine=ENGINE, instance=instance, sequence=request.sequence)
        raise HTTPException(500, "Local synthesis failed; source is not logged") from None
    finally:
        with gate:
            retired = max(retired, request.sequence)
            active = 0
            reserved = False
