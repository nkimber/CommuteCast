"""Installed English Piper voices, with one bounded ONNX session at a time."""
import gc
import json
import re
from pathlib import Path

from piper import PiperVoice
from piper.config import PiperConfig


class PiperVoiceLibrary:
    def __init__(self, folder, session_factory):
        self.folder = Path(folder)
        self.session_factory = session_factory
        self.configs = {}
        self.current_id = None
        self.current = None
        for path in sorted(self.folder.glob("*.onnx.json")):
            voice_id = path.name.removesuffix(".onnx.json")
            if not re.fullmatch(r"en_(US|GB)-[a-z0-9_]+-(x_low|low|medium|high)", voice_id):
                raise ValueError("Unsupported installed Piper voice identity")
            if not (self.folder / (voice_id + ".onnx")).is_file():
                raise ValueError("Installed Piper voice has no model")
            config = PiperConfig.from_dict(json.loads(path.read_text(encoding="utf-8")))
            if config.num_speakers != 1:
                raise ValueError("Installed Piper voice requires an unsupported speaker selection")
            self.configs[voice_id] = config
        if not self.configs:
            raise ValueError("No installed Piper voices")
        self.voice_ids = sorted(self.configs)

    def select(self, voice_id):
        if voice_id not in self.configs:
            raise ValueError("Piper voice is not installed")
        if self.current_id != voice_id or self.current is None:
            # Release the previous session before loading another voice. Loading
            # the whole catalog would exceed the owned service's 1 GiB limit.
            self.current = None
            self.current_id = None
            gc.collect()
            session = self.session_factory(self.folder / (voice_id + ".onnx"))
            self.current = PiperVoice(session=session, config=self.configs[voice_id])
            self.current_id = voice_id
        return self.current
