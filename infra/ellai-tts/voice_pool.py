"""Bounded local-only voice inventory; configuration never downloads a checkpoint."""
from collections import OrderedDict
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from threading import Lock
import json
import re


@dataclass(frozen=True)
class VoiceProfile:
    voice_id: str
    language: str
    model_id: str
    revision: str
    sha256: str
    directory: Path


def read_profiles(raw: str, english: VoiceProfile) -> dict[str, VoiceProfile]:
    profiles = {english.voice_id: english}
    entries = json.loads(raw)
    if not isinstance(entries, list) or len(entries) > 3:
        raise ValueError("At most three native voice profiles may be configured.")
    languages = {"eng"}
    for item in entries:
        language = item["language"]
        voice_id = item["voiceId"]
        if language not in {"sna", "nya", "tso"} or language in languages:
            raise ValueError("Unsupported or duplicate speech language.")
        if not re.fullmatch(r"[a-z0-9-]{1,80}", voice_id) or voice_id in profiles:
            raise ValueError("Invalid or duplicate voice identifier.")
        if item["modelId"] != f"facebook/mms-tts-{language}":
            raise ValueError("Checkpoint does not match its declared language.")
        if not re.fullmatch(r"[a-f0-9]{40}", item["revision"]) or not re.fullmatch(r"[a-f0-9]{64}", item["sha256"]):
            raise ValueError("A pinned revision and model hash are required.")
        directory = Path(item["directory"])
        if not directory.is_absolute():
            raise ValueError("Model directories must be absolute local paths.")
        profiles[voice_id] = VoiceProfile(voice_id, language, item["modelId"], item["revision"], item["sha256"], directory)
        languages.add(language)
    return profiles


class VoiceBusy(Exception):
    pass


class VoicePool:
    """One load/inference owner prevents duplicate loads and eviction during use."""
    def __init__(self, profiles, loader, capacity=2):
        if capacity < 1 or capacity > 4:
            raise ValueError("Voice cache capacity must be between one and four.")
        self.profiles = profiles
        self.loader = loader
        self.capacity = capacity
        self.cache = OrderedDict()
        self.lock = Lock()

    @contextmanager
    def use(self, voice_id):
        profile = self.profiles[voice_id]
        if not self.lock.acquire(blocking=False):
            raise VoiceBusy()
        try:
            if voice_id not in self.cache:
                # Evict before loading so the resident model bound also holds during loading.
                if len(self.cache) >= self.capacity:
                    self.cache.popitem(last=False)
                self.cache[voice_id] = self.loader(profile)
            self.cache.move_to_end(voice_id)
            yield profile, self.cache[voice_id]
        finally:
            self.lock.release()
