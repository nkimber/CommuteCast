"""Bounded local dictation. No audio files or transcripts are persisted/logged."""
import asyncio
import io
import wave
from contextlib import asynccontextmanager
from threading import Lock

import numpy as np
from fastapi import FastAPI, HTTPException, Request
from faster_whisper import WhisperModel

MAX_SECONDS = 120
MAX_BYTES = 16000 * 2 * MAX_SECONDS + 4096
MODEL_REVISION = "d1d751a5f8271d482d14ca55d9e2deeebbae577f"
gate = Lock()
model = None


@asynccontextmanager
async def lifespan(app):
    global model
    model = await asyncio.to_thread(WhisperModel, "/models/small.en", device="cpu", compute_type="int8", cpu_threads=4, num_workers=1)
    yield
    model = None


app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None)


@app.get("/health")
def health():
    return {"service": "commutecast-whisper", "contract": 1, "state": "ready" if model else "loading",
            "model": "small.en", "revision": MODEL_REVISION, "device": "cpu", "compute_type": "int8"}


def decode_wav(payload):
    try:
        with wave.open(io.BytesIO(payload), "rb") as audio:
            if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getcomptype()) != (1, 2, 16000, "NONE"):
                raise ValueError()
            frames = audio.getnframes()
            if not 1600 <= frames <= 16000 * MAX_SECONDS:
                raise ValueError()
            pcm = audio.readframes(frames)
            if len(pcm) != frames * 2:
                raise ValueError()
            return np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
    except (wave.Error, EOFError, ValueError):
        raise HTTPException(400, "Use a complete mono 16 kHz PCM16 WAV between 0.1 and 120 seconds.")


def transcribe(audio):
    # Release inside the worker: cancelling HTTP cannot admit a second inference.
    try:
        segments, _ = model.transcribe(audio, language="en", beam_size=5, vad_filter=True,
                                       condition_on_previous_text=False)
        return {"text": " ".join(segment.text.strip() for segment in segments).strip()}
    finally:
        gate.release()


@app.post("/transcribe")
async def transcription(request: Request):
    if request.headers.get("content-type", "").split(";")[0] != "audio/wav":
        raise HTTPException(415, "Send audio/wav.")
    payload = bytearray()
    async for chunk in request.stream():
        if len(payload) + len(chunk) > MAX_BYTES:
            raise HTTPException(413, "Recording exceeds two minutes.")
        payload.extend(chunk)
    audio = decode_wav(payload)
    if model is None:
        raise HTTPException(503, "Model is loading.")
    if not gate.acquire(blocking=False):
        raise HTTPException(409, "Transcription is busy. Try again shortly.")
    # Shield the submitted job so request cancellation cannot cancel a queued
    # executor future before its worker gets a chance to release the gate.
    worker = asyncio.create_task(asyncio.to_thread(transcribe, audio))
    return await asyncio.shield(worker)
