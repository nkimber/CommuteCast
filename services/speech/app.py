"""CommuteCast contract v1. No text logs, network inference, or runtime downloads."""
import hashlib
import io
import json
import logging
import os
import threading
import wave
import uuid
from contextlib import asynccontextmanager
from pathlib import Path

import numpy as np
import onnxruntime as ort
import soundfile as sf
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel, Field

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
            from piper import PiperVoice
            from piper.config import PiperConfig
            session = cpu_session(folder / "en_US-lessac-medium.onnx")
            config = json.loads((folder / "en_US-lessac-medium.onnx.json").read_text())
            model = PiperVoice(session=session, config=PiperConfig.from_dict(config))
            voices = ["en_US-lessac-medium"]
        else:
            raise ValueError("unsupported engine")
        actual = session.get_session_options()
        execution = {"provider": session.get_providers()[0], "intraOpThreads": actual.intra_op_num_threads,
                     "interOpThreads": actual.inter_op_num_threads, "mode": actual.execution_mode.name,
                     "intraOpSpinning": actual.get_session_config_entry("session.intra_op.allow_spinning"),
                     "interOpSpinning": actual.get_session_config_entry("session.inter_op.allow_spinning")}
        state = "ready"
    except Exception:
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
                "admission": 1, "instance": instance, "sequence": sequence}


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


class SpeechRequest(AdmissionRequest):
    text: str = Field(min_length=1, max_length=900)
    voice: str
    speed: float = Field(ge=0.7, le=1.4)
    fingerprint: str


@app.post("/speech")
def speech(request: SpeechRequest):
    global active, retired, reserved
    with gate:
        admission_identity(request)
        if state != "ready":
            raise HTTPException(503, "Model is not ready")
        if request.voice not in voices:
            raise HTTPException(409, "Voice or model identity changed")
        if active:
            raise HTTPException(429, "Inference is active; wait for quiescence")
        if not reserved or request.sequence != sequence or request.sequence <= retired:
            raise HTTPException(409, "Inference reservation is retired or missing")
        active = 1
    try:
        output = io.BytesIO()
        if ENGINE == "kokoro":
            # create() splits long phoneme sequences internally; the desktop sends small chunks.
            language = "en-gb" if request.voice.startswith("b") else "en-us"
            samples, rate = model.create(request.text, voice=request.voice, speed=request.speed, lang=language)
            sf.write(output, np.asarray(samples), rate, format="WAV", subtype="PCM_16")
        else:
            from piper import SynthesisConfig
            with wave.open(output, "wb") as wav:
                model.synthesize_wav(request.text, wav, syn_config=SynthesisConfig(length_scale=1 / request.speed))
        return Response(output.getvalue(), media_type="audio/wav")
    except Exception:
        raise HTTPException(500, "Local synthesis failed; source is not logged") from None
    finally:
        with gate:
            retired = max(retired, request.sequence)
            active = 0
            reserved = False
