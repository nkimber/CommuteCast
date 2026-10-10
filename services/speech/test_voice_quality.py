"""Contract tests against installed adapter dependencies; real-model trials are separate."""
import importlib.util
import io
import sys
import unittest
from unittest.mock import patch

import numpy as np
import soundfile as sf
from pydantic import ValidationError

SOURCE = sys.argv.pop(1) if len(sys.argv) > 1 else "/app/app.py"


class VoiceQualityTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("voice_quality_fixture", SOURCE)
        self.api = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = self.api
        spec.loader.exec_module(self.api)
        self.api.ENGINE = "kokoro"
        self.api.state = "ready"
        self.api.fingerprint = "kokoro:contract-v1:" + "a" * 64
        self.api.voices = ["af_heart", "af_bella", "bf_emma"]
        self.calls = []
        def create(text, **options):
            self.calls.append(options)
            return np.full(2400, .1), 24000
        self.api.model = type("Model", (), {"create": staticmethod(create), "get_voice_style": staticmethod(lambda voice: np.full((510, 1, 256), 1 if voice == "af_heart" else 3, dtype=np.float32))})()

    def request(self, local=None):
        return self.api.SpeechRequest(instance=self.api.instance, sequence=1, fingerprint=self.api.fingerprint,
                                      text="Hello.", voice=self.api.voices[0], speed=1, localVoice=local)

    def render(self, request):
        self.api.reserve(request)
        return self.api.speech(request)

    def test_blend_is_fixed_and_gain_applies_to_preview_and_saved_audio(self):
        result = self.render(self.request({"blendVoice": "af_bella", "blendWeight": .25, "gainDb": -6}))
        np.testing.assert_allclose(self.calls[0]["voice"], 1.5)
        self.assertFalse(self.calls[0]["trim"])
        samples, rate = sf.read(io.BytesIO(result.body))
        self.assertEqual(24000, rate)
        np.testing.assert_allclose(samples, .1 * 10 ** (-6 / 20), atol=1 / 32768)

    def test_legacy_request_retains_original_voice_and_trim_behavior(self):
        self.render(self.request())
        self.assertEqual("af_heart", self.calls[0]["voice"])
        self.assertNotIn("trim", self.calls[0])

    def test_invalid_engine_and_cross_accent_profiles_do_not_enter_inference(self):
        for local in ({"blendVoice": "bf_emma"}, {"blendVoice": "af_heart"}, {"blendVoice": "missing"}, {"noiseScale": .5}):
            with self.assertRaises(self.api.HTTPException):
                self.render(self.request(local))
            self.api.reserved = False
            self.api.sequence = 0
        self.assertEqual([], self.calls)
        self.assertEqual(0, self.api.active)

    def test_out_of_range_nonfinite_and_unknown_options_are_rejected(self):
        for local in ({"blendWeight": float("nan")}, {"noiseWidth": float("inf")}, {"gainDb": 1}, {"version": 2}, {"unknown": 1}):
            with self.assertRaises(ValidationError):
                self.request(local)

    def test_piper_receives_variation_raw_dynamics_and_gain(self):
        self.api.ENGINE = "piper"
        self.api.voices = ["en_US-lessac-medium"]
        calls = []
        def synthesize(text, wav, syn_config):
            calls.append(syn_config)
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(22050); wav.writeframes(bytes(4410))
        voice = type("Voice", (), {"synthesize_wav": staticmethod(synthesize)})()
        self.api.model = type("Library", (), {"select": staticmethod(lambda name: voice)})()
        self.render(self.request({"noiseScale": .55, "noiseWidth": .65, "gainDb": -3}))
        self.assertEqual(.55, calls[0].noise_scale)
        self.assertEqual(.65, calls[0].noise_w_scale)
        self.assertFalse(calls[0].normalize_audio)
        self.assertAlmostEqual(10 ** (-3 / 20), calls[0].volume)


if __name__ == "__main__":
    unittest.main(verbosity=2)
