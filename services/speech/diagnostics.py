"""Structured stderr diagnostics without scripts, exception messages or frame locals."""
import json
import logging
import traceback
from datetime import datetime, timezone


class JsonFormatter(logging.Formatter):
    def format(self, record):
        event = {"timestamp": datetime.now(timezone.utc).isoformat(),
                 "level": record.levelname, "event": record.getMessage(),
                 **getattr(record, "details", {})}
        return json.dumps(event)


logger = logging.getLogger("commutecast.speech")
logger.setLevel(logging.INFO)
logger.propagate = False
if not logger.handlers:
    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())
    logger.addHandler(handler)


def emit(event, level=logging.INFO, **details):
    logger.log(level, event, extra={"details": details})


def failure(event, error, **details):
    frames = [{"method": frame.name, "line": frame.lineno}
              for frame in traceback.extract_tb(error.__traceback__)[-40:]]
    emit(event, logging.ERROR, exception_type=type(error).__name__, frames=frames, **details)
