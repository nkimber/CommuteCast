import io
import unittest
import wave
import asyncio
import threading
from concurrent.futures import ThreadPoolExecutor
from fastapi import HTTPException
from app import decode_wav, transcribe, gate, MAX_BYTES
import app


def wav(seconds=1, rate=16000, channels=1):
    stream = io.BytesIO()
    with wave.open(stream, "wb") as writer:
        writer.setnchannels(channels)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(bytes(int(seconds * rate) * channels * 2))
    return stream.getvalue()


class AudioContractTests(unittest.TestCase):
    def test_accepts_recording_limit(self):
        payload = wav(120)
        self.assertLessEqual(len(payload), MAX_BYTES)
        self.assertEqual(1920000, len(decode_wav(payload)))

    def test_rejects_invalid_audio(self):
        for payload in (b"invalid", wav(0.01), wav(121), wav(rate=44100), wav(channels=2), wav()[:-2]):
            with self.assertRaises(HTTPException):
                decode_wav(payload)

    def test_failed_inference_releases_gate(self):
        class Failure:
            def transcribe(self, *args, **kwargs):
                raise RuntimeError("test")
        old = app.model
        app.model = Failure()
        gate.acquire()
        try:
            with self.assertRaises(RuntimeError):
                transcribe(decode_wav(wav()))
            self.assertFalse(gate.locked())
        finally:
            app.model = old


class EndpointTests(unittest.IsolatedAsyncioTestCase):
    class Request:
        def __init__(self, payload, content_type="audio/wav"):
            self.payload = payload
            self.headers = {"content-type": content_type}

        async def stream(self):
            yield self.payload

    async def test_rejects_unsupported_and_oversized_requests(self):
        for request, status in ((self.Request(wav(), "text/plain"), 415),
                                (self.Request(bytes(MAX_BYTES + 1)), 413),
                                (self.Request(b"bad"), 400)):
            with self.assertRaises(HTTPException) as result:
                await app.transcription(request)
            self.assertEqual(status, result.exception.status_code)

    async def test_loading_and_busy(self):
        old = app.model
        app.model = None
        try:
            with self.assertRaises(HTTPException) as result:
                await app.transcription(self.Request(wav()))
            self.assertEqual(503, result.exception.status_code)
            app.model = object()
            gate.acquire()
            try:
                with self.assertRaises(HTTPException) as result:
                    await app.transcription(self.Request(wav()))
                self.assertEqual(409, result.exception.status_code)
            finally:
                gate.release()
        finally:
            app.model = old

    async def test_cancelled_request_keeps_inference_gate_until_worker_finishes(self):
        entered = threading.Event()
        release = threading.Event()

        class BlockingModel:
            def transcribe(self, *args, **kwargs):
                entered.set()
                release.wait(5)
                return [], None

        old = app.model
        app.model = BlockingModel()
        task = asyncio.create_task(app.transcription(self.Request(wav())))
        try:
            for _ in range(100):
                if entered.is_set():
                    break
                await asyncio.sleep(0.01)
            self.assertTrue(entered.is_set())
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertTrue(gate.locked())
        finally:
            release.set()
            for _ in range(100):
                if not gate.locked():
                    break
                await asyncio.sleep(0.01)
            app.model = old
        self.assertFalse(gate.locked())

    async def test_cancellation_before_worker_starts_still_releases_gate(self):
        asyncio.get_running_loop().set_default_executor(ThreadPoolExecutor(max_workers=1))
        occupied = threading.Event()
        release = threading.Event()

        def occupy_executor():
            occupied.set()
            release.wait(5)

        class EmptyModel:
            def transcribe(self, *args, **kwargs):
                return [], None

        old = app.model
        app.model = EmptyModel()
        blocker = asyncio.create_task(asyncio.to_thread(occupy_executor))
        try:
            for _ in range(100):
                if occupied.is_set():
                    break
                await asyncio.sleep(0.01)
            self.assertTrue(occupied.is_set())
            task = asyncio.create_task(app.transcription(self.Request(wav())))
            for _ in range(100):
                if gate.locked():
                    break
                await asyncio.sleep(0.01)
            self.assertTrue(gate.locked())
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        finally:
            release.set()
            await blocker
            for _ in range(100):
                if not gate.locked():
                    break
                await asyncio.sleep(0.01)
            app.model = old
        self.assertFalse(gate.locked())


if __name__ == "__main__":
    unittest.main()
