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
    assert response.headers["content-type"].startswith("application/vnd.swiftaid.pcm")
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


def test_native_voice_metadata_and_language_mismatch(monkeypatch):
    from pathlib import Path
    from voice_pool import VoiceProfile
    with client(monkeypatch) as test_client:
        profile = VoiceProfile("shona-v1", "sna", "facebook/mms-tts-sna", "a" * 40, "b" * 64, Path("/models/sna"))
        pool = tts_app.app.state.pool
        pool.profiles[profile.voice_id] = profile
        pool.loader = lambda _: tts_app.LoadedVoice(FakeModel(), FakeTokenizer(), 22050)
        payload = {"text": "Synthetic test", "requestId": "native-1", "voiceId": profile.voice_id, "language": "sna"}
        response = test_client.post("/v1/synthesize", headers={"Authorization": "Bearer test-only-key"}, json=payload)
        assert response.status_code == 200
        assert response.headers["x-swiftaid-model-id"] == profile.model_id
        assert response.headers["x-swiftaid-language"] == "sna"
        assert response.headers["x-swiftaid-sample-rate"] == "22050"
        payload["language"] = "eng"
        assert test_client.post("/v1/synthesize", headers={"Authorization": "Bearer test-only-key"}, json=payload).status_code == 422


def test_busy_worker_applies_backpressure(monkeypatch):
    with client(monkeypatch) as test_client, tts_app.app.state.pool.use(tts_app.VOICE_ID):
        response = test_client.post("/v1/synthesize", headers={"Authorization": "Bearer test-only-key"},
                                    json={"text": "Synthetic test", "requestId": "busy-1"})
        assert response.status_code == 429
        assert response.headers["retry-after"] == "1"


def test_synthesis_uses_native_calm_speaking_rate(monkeypatch):
    calls = []
    original = FakeModel.__call__

    def capture(self, **inputs):
        calls.append(inputs.get("speaking_rate"))
        return original(self, **inputs)

    monkeypatch.setattr(FakeModel, "__call__", capture)
    with client(monkeypatch) as test_client:
        response = test_client.post(
            "/v1/synthesize",
            headers={"Authorization": "Bearer test-only-key"},
            json={"text": "Take your time, please. Ready? Let us begin!", "requestId": "calm-1"},
        )
    assert response.status_code == 200
    assert calls == [0.95]
    # Three model samples remain three PCM samples: no punctuation or trailing padding.
    assert len(response.content) == 3 * 2
    assert response.headers["x-swiftaid-speaking-rate"] == "0.95"
    assert response.headers["x-swiftaid-sample-rate"] == "16000"
