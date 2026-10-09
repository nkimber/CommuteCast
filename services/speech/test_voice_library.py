"""Synthetic catalog/session tests; run inside the pinned speech image."""
import gc
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import weakref

import voice_library


class VoiceLibraryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.folder = Path(self.directory.name)
        self.add_voice("en_US-lessac-medium")
        self.add_voice("en_GB-alba-medium")
        self.calls = []
        self.references = []

    def tearDown(self):
        self.directory.cleanup()

    def add_voice(self, name, speakers=1):
        (self.folder / (name + ".onnx")).write_bytes(b"synthetic model")
        config = {"audio": {"sample_rate": 22050}, "espeak": {"voice": "en-us"},
                  "num_symbols": 3, "num_speakers": speakers, "phoneme_id_map": {"a": [1]},
                  "phoneme_type": "espeak", "inference": {"noise_scale": 0.667, "length_scale": 1, "noise_w": 0.8}}
        (self.folder / (name + ".onnx.json")).write_text(json.dumps(config))

    def factory(self, path):
        gc.collect()
        self.assertTrue(all(reference() is None for reference in self.references))
        self.calls.append(path.name)
        return object()

    def voice(self, **kwargs):
        class StubVoice:
            pass
        result = StubVoice()
        result.session = kwargs["session"]
        self.references.append(weakref.ref(result))
        return result

    def test_catalog_does_not_load_sessions_and_repeated_voice_reuses_session(self):
        library = voice_library.PiperVoiceLibrary(self.folder, self.factory)
        self.assertEqual(["en_GB-alba-medium", "en_US-lessac-medium"], library.voice_ids)
        self.assertEqual([], self.calls)
        with patch.object(voice_library, "PiperVoice", side_effect=self.voice):
            first = library.select("en_US-lessac-medium")
            self.assertIs(first, library.select("en_US-lessac-medium"))
        self.assertEqual(["en_US-lessac-medium.onnx"], self.calls)

    def test_switch_releases_previous_voice_before_loading_and_never_falls_back(self):
        library = voice_library.PiperVoiceLibrary(self.folder, self.factory)
        with patch.object(voice_library, "PiperVoice", side_effect=self.voice):
            library.select("en_US-lessac-medium")
            library.select("en_GB-alba-medium")
            library.select("en_US-lessac-medium")
            with self.assertRaises(ValueError):
                library.select("en_US-not_installed-medium")
        self.assertEqual(["en_US-lessac-medium.onnx", "en_GB-alba-medium.onnx", "en_US-lessac-medium.onnx"], self.calls)

    def test_failed_load_can_retry_without_claiming_a_selected_voice(self):
        library = voice_library.PiperVoiceLibrary(self.folder, lambda path: (_ for _ in ()).throw(RuntimeError("fixture")))
        with self.assertRaises(RuntimeError):
            library.select("en_US-lessac-medium")
        self.assertIsNone(library.current_id)
        self.assertIsNone(library.current)

    def test_incomplete_voice_is_refused(self):
        (self.folder / "en_GB-alba-medium.onnx").unlink()
        with self.assertRaises(ValueError):
            voice_library.PiperVoiceLibrary(self.folder, self.factory)

    def test_unsupported_speaker_or_identity_is_refused(self):
        for identity, speakers in (("en_US-other-medium", 2), ("fr_FR-other-medium", 1)):
            with self.subTest(identity=identity):
                self.add_voice(identity, speakers)
                with self.assertRaises(ValueError):
                    voice_library.PiperVoiceLibrary(self.folder, self.factory)
                (self.folder / (identity + ".onnx")).unlink()
                (self.folder / (identity + ".onnx.json")).unlink()


if __name__ == "__main__":
    unittest.main(verbosity=2)
