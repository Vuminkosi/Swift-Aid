import numpy as np
import torch
from fastapi.testclient import TestClient

import app as tts_app


class FakeTokenizer:
    def __call__(self, **_):
        return {"input_ids": torch.tensor([[1, 2, 3]])}


class FakeModel:
    def __call__(self, **_):
        return type("Output", (), {"waveform": torch.tensor([[0.0, 0.5, -0.5]])})()


def client(monkeypatch):
    monkeypatch.setenv("ELLAI_TTS_INTERNAL_KEY", "test-only-key")
    monkeypatch.setattr(
        tts_app,
        "load_voice",
        lambda _: tts_app.LoadedVoice(FakeModel(), FakeTokenizer(), 16_000),
    )
    return TestClient(tts_app.app)


def test_readiness_and_synthesis_require_internal_key(monkeypatch):
    with client(monkeypatch) as test_client:
        assert test_client.get("/health/ready").status_code == 200
        response = test_client.post(
            "/v1/synthesize",
            json={"text": "A safe public sentence.", "requestId": "test-1"},
        )

    assert response.status_code == 401


def test_synthesis_returns_pinned_pcm_contract(monkeypatch):
    with client(monkeypatch) as test_client:
        response = test_client.post(
            "/v1/synthesize",
            headers={"Authorization": "Bearer test-only-key"},
            json={"text": "A safe public sentence.", "requestId": "test-2"},
        )

    assert response.status_code == 200
    assert response.headers["content-type"].startswith("audio/L16")
    assert response.headers["x-swiftaid-sample-rate"] == "16000"
    assert response.headers["x-swiftaid-channels"] == "1"
    assert response.headers["x-swiftaid-sample-format"] == "s16le"
    assert response.headers["x-swiftaid-voice-id"] == tts_app.VOICE_ID
    assert response.content == np.array([0, 16383, -16383], dtype="<i2").tobytes()


def test_synthesis_rejects_unconfigured_voice(monkeypatch):
    with client(monkeypatch) as test_client:
        response = test_client.post(
            "/v1/synthesize",
            headers={"Authorization": "Bearer test-only-key"},
            json={
                "text": "A safe public sentence.",
                "voiceId": "another-voice",
                "requestId": "test-3",
            },
        )

    assert response.status_code == 422
