"""CommuteCast contract v1. No text logs, network inference, or runtime downloads."""
import hashlib
import io
import logging
import os
import threading
import wave
from contextlib import asynccontextmanager
from pathlib import Path

import numpy as np
import soundfile as sf
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel, Field

ENGINE = os.environ.get("COMMUTECAST_ENGINE", "kokoro")
state = "loading"
model = None
voices = []
fingerprint = ""
active = 0
gate = threading.Lock()
logging.getLogger("kokoro_onnx").setLevel(logging.CRITICAL)
logging.getLogger("phonemizer").setLevel(logging.CRITICAL)


def load():
    global model, voices, fingerprint, state
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
            model = Kokoro(str(folder / "kokoro-v1.0.onnx"), str(folder / "voices-v1.0.bin"))
            voices = sorted(v for v in model.get_voices() if v.startswith(("af_", "am_", "bf_", "bm_")))
        elif ENGINE == "piper":
            from piper import PiperVoice
            model = PiperVoice.load(str(folder / "en_US-lessac-medium.onnx"))
            voices = ["en_US-lessac-medium"]
        else:
            raise ValueError("unsupported engine")
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
    return {"service": "CommuteCast", "contract": 1, "engine": ENGINE,
            "fingerprint": fingerprint, "voices": voices, "state": state, "active": active}


class SpeechRequest(BaseModel):
    text: str = Field(min_length=1, max_length=900)
    voice: str
    speed: float = Field(ge=0.7, le=1.4)
    fingerprint: str


@app.post("/speech")
def speech(request: SpeechRequest):
    global active
    if state != "ready":
        raise HTTPException(503, "Model is not ready")
    if request.fingerprint != fingerprint or request.voice not in voices:
        raise HTTPException(409, "Voice or model identity changed")
    if not gate.acquire(blocking=False):
        raise HTTPException(429, "Inference is active; wait for quiescence")
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
        active = 0
        gate.release()
