"""Pinned, local-only Kokoro English adapter; no microphone or model downloads."""
from pathlib import Path
import hashlib
import os
import re

import numpy as np

MODEL_ID = "hexgrad/Kokoro-82M"
MODEL_SHA256 = "496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
CONFIG_SHA256 = "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"
HEART_SHA256 = "0ab5709b8ffab19bfd849cd11d98f75b60af7733253ad0d67b12382a102cb4ff"
SAMPLE_RATE = 24000
SPEED = 0.95


def verify_file(path: Path, expected: str) -> None:
    if not re.fullmatch(r"[a-f0-9]{64}", expected):
        raise ValueError("A pinned artifact hash is required.")
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != expected:
        raise ValueError("Kokoro artifact does not match its pinned hash.")


class KokoroVoice:
    """Called under the existing voice pool's exclusive inference lock."""
    sample_rate = SAMPLE_RATE

    def __init__(self, directory: Path, voice_id: str = "af_heart", voice_sha256: str = HEART_SHA256):
        if not re.fullmatch(r"[ab][fm]_[a-z0-9]+", voice_id):
            raise ValueError("Only installed English Kokoro voices are supported.")
        model_file = directory / "kokoro-v1_0.pth"
        config_file = directory / "config.json"
        self.voice_file = directory / "voices" / f"{voice_id}.pt"
        verify_file(model_file, MODEL_SHA256)
        verify_file(config_file, CONFIG_SHA256)
        verify_file(self.voice_file, voice_sha256)
        # Imported only when explicitly configured, so MMS-only deployments need
        # no Kokoro packages. Assets are checked before any model code executes.
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        import spacy.util
        if not spacy.util.is_package("en_core_web_sm"):
            raise RuntimeError("The cached English linguistic model is unavailable.")
        from kokoro import KModel, KPipeline
        self.model = KModel(repo_id=MODEL_ID, config=str(config_file), model=str(model_file)).to("cpu").eval()
        self.pipeline = KPipeline(lang_code=voice_id[0], repo_id=MODEL_ID,
                                  model=self.model, trf=False, device="cpu")

    def synthesize(self, text: str) -> np.ndarray:
        if not text.strip() or len(text) > 2000:
            raise ValueError("Invalid speech text length.")
        import torch
        chunks = []
        samples = 0
        with torch.inference_mode():
            for result in self.pipeline(text, voice=str(self.voice_file), speed=SPEED):
                if result.audio is None:
                    continue
                chunk = result.audio.detach().cpu().numpy()
                samples += chunk.size
                if chunk.ndim != 1 or not np.isfinite(chunk).all() or samples > SAMPLE_RATE * 120:
                    raise ValueError("Invalid or excessive Kokoro waveform.")
                chunks.append(chunk)
        if samples == 0:
            raise ValueError("Kokoro produced no audio.")
        return np.concatenate(chunks).astype(np.float32, copy=False)
