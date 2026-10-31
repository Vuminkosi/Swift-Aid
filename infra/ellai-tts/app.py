from __future__ import annotations

import hmac
import os
import re
from collections.abc import Iterator
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
from fastapi import FastAPI, Header, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field
from transformers import VitsModel, VitsTokenizer


EXPECTED_MODEL_ID = "facebook/mms-tts-eng"
EXPECTED_REVISION = "c71de0fe7204c83f1c10820a7d696d0b450048ba"
EXPECTED_MODEL_SHA256 = "69cf8b651c1493f1801dfd2311c298d694a38357bc9a1e41f410491ea6f0e1be"
VOICE_ID = "swiftaid-mms-english-v1"
MAX_TEXT_LENGTH = 2000
MAX_SAMPLE_COUNT = 16_000 * 60 * 2
FRAME_BYTES = 32 * 1024
_MARKUP = re.compile(r"<[^>]*>")


@dataclass(frozen=True)
class LoadedVoice:
    model: VitsModel
    tokenizer: VitsTokenizer
    sample_rate: int


class SynthesisRequest(BaseModel):
    text: str = Field(min_length=1, max_length=MAX_TEXT_LENGTH)
    voice_id: str = Field(default=VOICE_ID, min_length=1, max_length=80, alias="voiceId")
    request_id: str = Field(min_length=1, max_length=120, alias="requestId")


def load_voice(model_dir: Path) -> LoadedVoice:
    import hashlib

    model_file = model_dir / "model.safetensors"
    if not model_file.is_file():
        raise RuntimeError("The pinned MMS-TTS model file is missing.")

    digest = hashlib.sha256()
    with model_file.open("rb") as model_stream:
        for chunk in iter(lambda: model_stream.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != EXPECTED_MODEL_SHA256:
        raise RuntimeError("The mounted MMS-TTS model file does not match the pinned SHA-256.")

    model = VitsModel.from_pretrained(
        str(model_dir), local_files_only=True, use_safetensors=True
    ).to("cpu").eval()
    tokenizer = VitsTokenizer.from_pretrained(str(model_dir), local_files_only=True)
    sample_rate = int(model.config.sampling_rate)
    if sample_rate <= 0:
        raise RuntimeError("The pinned MMS-TTS model has an invalid sample rate.")
    return LoadedVoice(model, tokenizer, sample_rate)


def _get_voice() -> LoadedVoice:
    voice = getattr(app.state, "voice", None)
    if voice is None:
        raise HTTPException(status_code=503, detail="Speech synthesis is unavailable.")
    return voice


@asynccontextmanager
async def lifespan(application: FastAPI) -> Iterator[None]:
    model_path = Path(os.environ.get("ELLAI_TTS_MODEL_PATH", "/models/mms-tts-eng"))
    try:
        application.state.voice = load_voice(model_path)
    except (OSError, RuntimeError, ValueError):
        application.state.voice = None
    yield


app = FastAPI(title="SwiftAid Ellai Gateway Speech Engine", lifespan=lifespan)


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/health/ready")
def ready() -> dict[str, str]:
    if not os.environ.get("ELLAI_TTS_INTERNAL_KEY") or getattr(app.state, "voice", None) is None:
        raise HTTPException(status_code=503, detail="The approved speech checkpoint is unavailable.")
    return {"status": "ready", "voiceId": VOICE_ID, "modelId": EXPECTED_MODEL_ID}


def _frames(pcm: bytes) -> Iterator[bytes]:
    for offset in range(0, len(pcm), FRAME_BYTES):
        yield pcm[offset : offset + FRAME_BYTES]


@app.post("/v1/synthesize")
def synthesize(
    request: SynthesisRequest,
    authorization: str | None = Header(default=None),
) -> StreamingResponse:
    secret = os.environ.get("ELLAI_TTS_INTERNAL_KEY", "")
    if not secret or not authorization or not hmac.compare_digest(
        authorization, f"Bearer {secret}"
    ):
        raise HTTPException(status_code=401, detail="Unauthorized.")

    if request.voice_id != VOICE_ID:
        raise HTTPException(status_code=422, detail="The requested voice profile is unavailable.")
    if _MARKUP.search(request.text) or not request.text.strip():
        raise HTTPException(status_code=422, detail="The speech text is invalid.")

    voice = _get_voice()
    inputs = voice.tokenizer(text=request.text, return_tensors="pt")
    with torch.inference_mode():
        waveform = voice.model(**inputs).waveform[0].detach().cpu().numpy()
    waveform = np.asarray(waveform, dtype=np.float32)
    if waveform.ndim != 1 or waveform.size == 0 or waveform.size > MAX_SAMPLE_COUNT:
        raise HTTPException(status_code=422, detail="The synthesized audio exceeds its limit.")
    pcm = (np.clip(waveform, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
    return StreamingResponse(
        _frames(pcm),
        media_type="application/vnd.swiftaid.pcm",
        headers={
            "X-SwiftAid-Voice-Id": VOICE_ID,
            "X-SwiftAid-Sample-Rate": str(voice.sample_rate),
            "X-SwiftAid-Channels": "1",
            "X-SwiftAid-Sample-Format": "s16le",
            "X-SwiftAid-Model-Id": EXPECTED_MODEL_ID,
            "X-SwiftAid-Model-Revision": EXPECTED_REVISION,
            "X-SwiftAid-Request-Id": request.request_id,
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
        },
    )
