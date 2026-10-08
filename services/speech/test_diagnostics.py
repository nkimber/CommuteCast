import io
import logging
import unittest
from diagnostics import JsonFormatter, failure, logger


class DiagnosticsTests(unittest.TestCase):
    def test_failure_does_not_leak_message_or_source(self):
        stream = io.StringIO()
        handler = logging.StreamHandler(stream)
        handler.setFormatter(JsonFormatter())
        logger.addHandler(handler)
        try:
            try:
                raise ValueError("PRIVATE SCRIPT")
            except ValueError as error:
                failure("synthesis_failed", error, instance="fixture", sequence=4)
            self.assertNotIn("PRIVATE", stream.getvalue())
            self.assertIn("ValueError", stream.getvalue())
            self.assertIn('"sequence": 4', stream.getvalue())
            self.assertIn('"method":', stream.getvalue())
        finally:
            logger.removeHandler(handler)


if __name__ == "__main__":
    unittest.main()
