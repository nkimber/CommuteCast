"""Contract tests run without installing models or Torch."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock
import compare_expressive as comparison


class ComparisonTests(unittest.TestCase):
    def test_qwen_requires_the_instruction_capable_model(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            for kind, size in [("base", "1b7"), ("custom_voice", "0b6")]:
                (folder / "config.json").write_text(json.dumps({"tts_model_type": kind, "tts_model_size": size}))
                with self.assertRaises(ValueError):
                    comparison.validate_qwen(folder)
            (folder / "config.json").write_text(json.dumps({"tts_model_type": "custom_voice", "tts_model_size": "1b7"}))
            comparison.validate_qwen(folder)

    def test_qwen_keeps_speaker_and_text_fixed_and_varies_only_instruction(self):
        model = Mock()
        model.generate_custom_voice.return_value = (["wave"], 24000)
        for expressive in [False, True]:
            self.assertEqual(("wave", 24000), comparison.synthesize_qwen(model, "Ryan", expressive))
            self.assertEqual({"text": comparison.SAMPLE, "language": "English", "speaker": "Ryan",
                              "instruct": comparison.INSTRUCTION if expressive else ""}, model.generate_custom_voice.call_args.kwargs)

    def test_nano_reuses_conditioning_and_only_uses_supported_controls(self):
        model = Mock(sr=24000)
        for expressive in [False, True]:
            comparison.synthesize_nano(model, expressive)
            args, kwargs = model.generate.call_args
            self.assertEqual(expressive, "[chuckle]" in args[0])
            self.assertEqual({"temperature", "top_p", "top_k", "repetition_penalty"}, set(kwargs))
        model.prepare_conditionals.assert_not_called()

    def test_inventory_is_stable_and_changes_with_model_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory); path = folder / "weight.bin"; path.write_bytes(b"first")
            before = comparison.model_inventory(folder)
            self.assertEqual(before, comparison.model_inventory(folder))
            path.write_bytes(b"second")
            self.assertNotEqual(before, comparison.model_inventory(folder))


if __name__ == "__main__":
    unittest.main()
