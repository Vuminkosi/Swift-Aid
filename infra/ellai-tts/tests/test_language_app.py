from fastapi.testclient import TestClient
import language_app


class Engine:
    def translate(self, request):
        return {'requestId': request.request_id, 'text': 'Hello', 'sourceLanguage': request.source_language,
                'targetLanguage': request.target_language, 'modelId': 'facebook/nllb-200-distilled-1.3B',
                'modelRevision': '7be3e24664b38ce1cac29b8aeed6911aa0cf0576', 'inputTokens': 3,
                'inferenceMilliseconds': 1, 'quality': 'unreviewed'}


def test_private_translation_requires_credential(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    assert TestClient(language_app.app).post('/v1/translate', json={}).status_code == 401


def test_translation_preserves_identity_and_unreviewed_status(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    language_app.app.state.engine = Engine()
    response = TestClient(language_app.app).post('/v1/translate', headers={'Authorization': 'Bearer test-secret'},
        json={'requestId': 'turn-1', 'text': 'Mhoro', 'sourceLanguage': 'sna', 'targetLanguage': 'eng'})
    assert response.status_code == 200
    assert response.json()['requestId'] == 'turn-1'
    assert response.json()['quality'] == 'unreviewed'
    assert response.headers['cache-control'] == 'no-store'


def test_unsupported_language_is_rejected_without_echoing_text(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    response = TestClient(language_app.app).post('/v1/translate', headers={'Authorization': 'Bearer test-secret'},
        json={'requestId': 'turn-1', 'text': 'private sentinel', 'sourceLanguage': 'nde', 'targetLanguage': 'eng'})
    assert response.status_code == 422
    assert 'private sentinel' not in response.text


def test_busy_is_retryable(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    class BusyEngine:
        def translate(self, request):
            raise language_app.ModelBusy()
    language_app.app.state.engine = BusyEngine()
    response = TestClient(language_app.app).post('/v1/translate', headers={'Authorization': 'Bearer test-secret'},
        json={'requestId': 'turn-1', 'text': 'Mhoro', 'sourceLanguage': 'sna', 'targetLanguage': 'eng'})
    assert response.status_code == 429
    assert response.headers['retry-after'] == '1'


def test_body_limit_is_enforced_before_json_parsing(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    response = TestClient(language_app.app).post('/v1/transcribe',
        headers={'Authorization': 'Bearer test-secret'}, content=b'x' * (language_app.MAX_BODY + 1))
    assert response.status_code == 413


def test_native_to_native_and_english_bypass_are_not_inference_routes(monkeypatch):
    monkeypatch.setenv('ELLAI_LANGUAGE_INTERNAL_KEY', 'test-secret')
    for source, target in [('sna', 'nya'), ('eng', 'eng')]:
        response = TestClient(language_app.app).post('/v1/translate', headers={'Authorization': 'Bearer test-secret'},
            json={'requestId': 'turn-1', 'text': 'Hello', 'sourceLanguage': source, 'targetLanguage': target})
        assert response.status_code == 422


def test_engine_rejects_invalid_pcm_before_loading_models(tmp_path):
    import pytest
    from fastapi import HTTPException
    from pathlib import Path
    engine = language_app.LanguageEngine(tmp_path, Path('../../scripts/ellai/native-models.lock.json'))
    request = language_app.TranscriptionRequest(requestId='turn', language='sna', pcmBase64='!!!!', sampleRate=16000)
    with pytest.raises(HTTPException) as error:
        engine.transcribe(request)
    assert error.value.status_code == 422
    assert engine.loaded is None


def test_engine_lock_rejects_overlapping_inference_and_releases_on_error(tmp_path):
    import pytest
    from pathlib import Path
    engine = language_app.LanguageEngine(tmp_path, Path('../../scripts/ellai/native-models.lock.json'))
    engine.lock.acquire()
    try:
        with pytest.raises(language_app.ModelBusy):
            with engine.use('facebook/mms-1b-all'):
                pass
    finally:
        engine.lock.release()
    with pytest.raises(RuntimeError):
        with engine.use('facebook/mms-1b-all'):
            pass
    assert not engine.lock.locked()
