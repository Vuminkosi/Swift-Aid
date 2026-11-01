"""Exercise real native checkpoints through the speech worker HTTP handler."""
import argparse
import json
import os
import secrets
import sys
import time
from pathlib import Path

import numpy as np
import torch
from fastapi.testclient import TestClient

from provision_native_models import verify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    args = parser.parse_args()
    torch.set_num_threads(4)
    root = Path(__file__).resolve().parents[2]
    sys.path.insert(0, str(root / 'infra' / 'ellai-tts'))
    os.environ['HF_HUB_OFFLINE'] = '1'
    os.environ['TRANSFORMERS_OFFLINE'] = '1'
    import app as speech
    from voice_pool import VoicePool, VoiceProfile

    manifest = json.loads(Path(__file__).with_name('native-models.lock.json').read_text())
    profiles = []
    for model in manifest['models']:
        if '/mms-tts-' not in model['model']:
            continue
        folder = args.directory.resolve() / model['model'].split('/')[1] / model['revision']
        if not all(verify(folder / a['name'], a) for a in model['files']):
            raise SystemExit(f"Missing or invalid artifacts: {model['model']}")
        weight = next(a for a in model['files'] if a['name'] == 'model.safetensors')
        language = model['model'].rsplit('-', 1)[1]
        profiles.append(VoiceProfile(f'swiftaid-mms-{language}-v1', language, model['model'],
                                     model['revision'], weight['sha256'], folder))
    speech.app.state.pool = VoicePool({p.voice_id: p for p in profiles}, lambda p: speech.load_voice(p.directory, p.sha256), 1)
    secret = secrets.token_hex(32)
    os.environ['ELLAI_TTS_INTERNAL_KEY'] = secret
    # No lifespan: English startup/readiness is outside this native endpoint check.
    client = TestClient(speech.app)
    for profile in profiles:
        started = time.monotonic()
        response = client.post('/v1/synthesize', headers={'Authorization': f'Bearer {secret}'},
            json={'text': {'sna': 'Mhoro.', 'nya': 'Moni.', 'tso': 'Avuxeni.'}[profile.language],
                  'voiceId': profile.voice_id, 'language': profile.language, 'requestId': 'native-smoke'})
        response.raise_for_status()
        pcm = np.frombuffer(response.content, dtype='<i2')
        assert pcm.size > 0 and np.any(pcm != 0), 'Empty or silent output'
        assert response.headers['x-swiftaid-model-revision'] == profile.revision
        assert response.headers['x-swiftaid-language'] == profile.language
        rate = int(response.headers['x-swiftaid-sample-rate'])
        assert rate > 0
        print(json.dumps({'language': profile.language, 'revision': profile.revision,
                          'sampleRate': rate, 'audioSeconds': round(pcm.size / rate, 3),
                          'elapsedSeconds': round(time.monotonic() - started, 3),
                          'result': 'PASS', 'pronunciation': 'NOT VERIFIED'}), flush=True)
    client.close()


if __name__ == '__main__':
    main()
