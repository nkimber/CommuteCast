"""Run inside the pinned speech image: python /tmp/test_admission.py /tmp/app.py.

The real adapter dependencies are imported; a blocked local model isolates
admission races without downloading models or claiming real ONNX acceptance.
"""
import importlib.util
import sys
import threading
import unittest

SOURCE = sys.argv.pop(1) if len(sys.argv) > 1 else "/app/app.py"


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("admission_fixture", SOURCE)
        self.api = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = self.api
        spec.loader.exec_module(self.api)
        self.api.ENGINE = "kokoro"
        self.api.state = "ready"
        self.api.fingerprint = "kokoro:contract-v1:" + "a" * 64
        self.api.voices = ["af_heart"]
        self.started = threading.Event()
        self.finished = threading.Event()
        self.calls = 0

        def create(*args, **kwargs):
            self.calls += 1
            self.started.set()
            if not self.finished.wait(5):
                raise RuntimeError("fixture deadline")
            return [0.01] * 2400, 24000

        self.api.model = type("LocalModel", (), {"create": staticmethod(create)})()

    def admission(self, sequence=1, instance=None):
        return self.api.AdmissionRequest(instance=instance or self.api.instance,
                                         sequence=sequence, fingerprint=self.api.fingerprint)

    def speech(self, sequence=1, instance=None):
        return self.api.SpeechRequest(**self.admission(sequence, instance).model_dump(),
                                      text="Private fixture source", voice="af_heart", speed=1)

    def refused(self, action, status=409):
        with self.assertRaises(self.api.HTTPException) as failure:
            action()
        self.assertEqual(status, failure.exception.status_code)

    def test_retirement_before_delayed_reserve_or_speech(self):
        self.assertEqual("settled", self.api.settle(self.admission())["state"])
        self.refused(lambda: self.api.reserve(self.admission()))
        self.refused(lambda: self.api.speech(self.speech()))
        self.assertEqual(0, self.calls)
        self.assertEqual("reserved", self.api.reserve(self.admission(2))["state"])

    def test_retirement_after_reserve_prevents_delayed_speech(self):
        self.api.reserve(self.admission())
        self.api.settle(self.admission())
        self.refused(lambda: self.api.speech(self.speech()))
        self.assertEqual(0, self.calls)

    def test_running_inference_remains_active_until_model_returns(self):
        self.api.reserve(self.admission())
        result = []
        worker = threading.Thread(target=lambda: result.append(self.api.speech(self.speech())))
        worker.start()
        try:
            self.assertTrue(self.started.wait(2))
            self.assertEqual(1, self.api.health()["active"])
            self.assertEqual("active", self.api.settle(self.admission())["state"])
            self.refused(lambda: self.api.reserve(self.admission(2)), 429)
            self.refused(lambda: self.api.speech(self.speech()), 429)
        finally:
            self.finished.set()
            worker.join(3)
        self.assertFalse(worker.is_alive())
        self.assertEqual(1, len(result))
        self.assertEqual("settled", self.api.settle(self.admission())["state"])
        self.refused(lambda: self.api.speech(self.speech()))
        self.assertEqual(1, self.calls)
        self.assertEqual(0, self.api.health()["active"])

    def test_old_settlement_does_not_erase_new_reservation(self):
        self.api.settle(self.admission())
        self.api.reserve(self.admission(2))
        self.api.settle(self.admission())
        self.assertTrue(self.api.reserved)
        self.finished.set()
        self.api.speech(self.speech(2))
        self.assertEqual(1, self.calls)

    def test_old_process_tokens_never_reserve_retire_or_synthesize(self):
        other = "b" * 32 if self.api.instance != "b" * 32 else "c" * 32
        for action in (lambda: self.api.reserve(self.admission(instance=other)),
                       lambda: self.api.settle(self.admission(instance=other)),
                       lambda: self.api.speech(self.speech(instance=other))):
            self.refused(action)
        self.assertEqual(0, self.api.sequence)
        self.assertEqual(0, self.calls)

    def test_retirement_is_bounded_high_water_not_an_expiring_tombstone(self):
        for number in range(1, 1001):
            self.api.settle(self.admission(number))
        self.refused(lambda: self.api.reserve(self.admission()))
        self.refused(lambda: self.api.speech(self.speech()))
        self.assertEqual(1000, self.api.sequence)
        self.assertEqual(1000, self.api.retired)

    def test_failed_model_still_retires_reservation(self):
        self.api.reserve(self.admission())
        self.api.model = type("FailedModel", (), {"create": staticmethod(lambda *a, **k: (_ for _ in ()).throw(RuntimeError("private source")))})()
        self.refused(lambda: self.api.speech(self.speech()), 500)
        self.assertEqual("settled", self.api.settle(self.admission())["state"])
        self.assertEqual(0, self.api.active)
        self.assertFalse(self.api.reserved)


if __name__ == "__main__":
    unittest.main(verbosity=2)
