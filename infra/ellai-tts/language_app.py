"""Private, bounded local NLLB/MMS-ASR inference. Durable ownership stays in Gateway."""
import base64
import binascii
import gc
import hmac
import json
import os
import time
from contextlib import asynccontextmanager, contextmanager
from pathlib import Path
from threading import Lock
from typing import Literal

import numpy as np
import torch
from fastapi import FastAPI, HTTPException
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, model_validator
from transformers import AutoModelForSeq2SeqLM, AutoProcessor, AutoTokenizer, Wav2Vec2ForCTC

from provision_native_models import verify

Language = Literal['eng', 'sna', 'nya', 'tso', 'ssw']
TOKENS = {code: code + '_Latn' for code in ['eng', 'sna', 'nya', 'tso', 'ssw']}
MAX_BODY = 2_600_000


class ModelBusy(Exception):
    pass


class TranslationRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    request_id: str = Field(alias='requestId', min_length=1, max_length=120)
    text: str = Field(min_length=1, max_length=6000)
    source_language: Language = Field(alias='sourceLanguage')
    target_language: Language = Field(alias='targetLanguage')

    @model_validator(mode='after')
    def supported_pair(self):
        if not self.text.strip() or self.source_language == self.target_language or 'eng' not in (self.source_language, self.target_language):
            raise ValueError('Unsupported translation pair')
        return self


class TranscriptionRequest(BaseModel):
    model_config = ConfigDict(extra='forbid')
    request_id: str = Field(alias='requestId', min_length=1, max_length=120)
    language: Literal['sna', 'nya', 'tso']
    pcm_base64: str = Field(alias='pcmBase64', min_length=4, max_length=2_560_000)
    sample_rate: Literal[16000] = Field(alias='sampleRate')


class LanguageEngine:
    def __init__(self, directory: Path, manifest: Path):
        self.directory = directory
        entries = json.loads(manifest.read_text())['models']
        self.models = {m['model']: m for m in entries if m['model'] in
                       ('facebook/nllb-200-distilled-1.3B', 'facebook/mms-1b-all')}
        if len(self.models) != 2:
            raise ValueError('Missing pinned provider inventory')
        self.lock = Lock()
        self.loaded_id = None
        self.loaded = None
        self.verified = False

    def validate_inventory(self):
        for model in self.models.values():
            folder = self.directory / model['model'].split('/')[1] / model['revision']
            if not all(verify(folder / artifact['name'], artifact) for artifact in model['files']):
                raise RuntimeError('Missing or invalid pinned model artifacts')
        self.verified = True

    @contextmanager
    def use(self, model_id):
        if not self.lock.acquire(blocking=False):
            raise ModelBusy()
        try:
            if not self.verified:
                raise RuntimeError('Provider inventory is not verified')
            if self.loaded_id != model_id:
                # One large model at a time, including while loading its replacement.
                self.loaded = None
                self.loaded_id = None
                gc.collect()
                model = self.models[model_id]
                folder = str(self.directory / model_id.split('/')[1] / model['revision'])
                if model_id == 'facebook/nllb-200-distilled-1.3B':
                    processor = AutoTokenizer.from_pretrained(folder, local_files_only=True)
                    network = AutoModelForSeq2SeqLM.from_pretrained(folder, local_files_only=True, dtype=torch.bfloat16).eval()
                else:
                    processor = AutoProcessor.from_pretrained(folder, local_files_only=True)
                    network = Wav2Vec2ForCTC.from_pretrained(folder, local_files_only=True,
                        target_lang='sna', dtype=torch.bfloat16).eval()
                self.loaded = (processor, network)
                self.loaded_id = model_id
            yield self.loaded
        finally:
            self.lock.release()

    def translate(self, request):
        model_id = 'facebook/nllb-200-distilled-1.3B'
        started = time.monotonic()
        with self.use(model_id) as (tokenizer, model):
            tokenizer.src_lang = TOKENS[request.source_language]
            inputs = tokenizer(request.text, return_tensors='pt', truncation=False)
            count = inputs.input_ids.shape[-1]
            if count > 512:
                raise HTTPException(422, 'translation-input-too-long')
            target = tokenizer.convert_tokens_to_ids(TOKENS[request.target_language])
            if target == tokenizer.unk_token_id:
                raise HTTPException(422, 'unsupported-language')
            with torch.inference_mode():
                output = model.generate(**inputs, forced_bos_token_id=target, max_new_tokens=512)
            if output[0, -1].item() != model.config.eos_token_id:
                raise HTTPException(422, 'translation-output-incomplete')
            text = tokenizer.batch_decode(output, skip_special_tokens=True)[0].strip()
            if not text:
                raise HTTPException(422, 'translation-output-empty')
        return {'requestId': request.request_id, 'text': text, 'sourceLanguage': request.source_language,
                'targetLanguage': request.target_language, 'modelId': model_id,
                'modelRevision': self.models[model_id]['revision'], 'inputTokens': count,
                'inferenceMilliseconds': (time.monotonic() - started) * 1000, 'quality': 'unreviewed'}

    def transcribe(self, request):
        try:
            raw = base64.b64decode(request.pcm_base64, validate=True)
        except (ValueError, binascii.Error):
            raise HTTPException(422, 'invalid-pcm') from None
        if len(raw) % 2 or not 3200 <= len(raw) <= 1_920_000:
            raise HTTPException(422, 'invalid-pcm-duration')
        audio = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0
        model_id = 'facebook/mms-1b-all'
        started = time.monotonic()
        with self.use(model_id) as (processor, model):
            # Adapter, tokenizer and inference share one lock; no cross-language races.
            processor.tokenizer.set_target_lang(request.language)
            model.load_adapter(request.language, local_files_only=True, force_load=False)
            inputs = processor(audio, sampling_rate=16000, return_tensors='pt')
            inputs = {k: v.to(torch.bfloat16) if v.is_floating_point() else v for k, v in inputs.items()}
            with torch.inference_mode():
                logits = model(**inputs).logits
            if not torch.isfinite(logits).all():
                raise HTTPException(503, 'transcription-output-invalid')
            text = processor.batch_decode(torch.argmax(logits, dim=-1))[0].strip()
        return {'requestId': request.request_id, 'text': text, 'language': request.language,
                'modelId': model_id, 'modelRevision': self.models[model_id]['revision'],
                'inferenceMilliseconds': (time.monotonic() - started) * 1000, 'quality': 'unreviewed'}


@asynccontextmanager
async def lifespan(application):
    torch.set_num_threads(max(1, min(8, int(os.environ.get('ELLAI_LANGUAGE_THREADS', '4')))))
    engine = LanguageEngine(Path(os.environ.get('ELLAI_LANGUAGE_MODEL_PATH', '/models')),
                            Path(os.environ.get('ELLAI_LANGUAGE_MANIFEST_PATH', str(Path(__file__).with_name('native-models.lock.json')))))
    engine.validate_inventory()
    application.state.engine = engine
    yield
    application.state.engine = None


app = FastAPI(title='SwiftAid private language inference', lifespan=lifespan)


class PrivateBodyBoundary:
    def __init__(self, app):
        self.inner = app

    async def __call__(self, scope, receive, send):
        if scope['type'] != 'http' or scope['path'].startswith('/health/'):
            return await self.inner(scope, receive, send)
        headers = dict(scope['headers'])
        secret = os.environ.get('ELLAI_LANGUAGE_INTERNAL_KEY', '')
        if not secret or not hmac.compare_digest(headers.get(b'authorization', b''), ('Bearer ' + secret).encode()):
            return await JSONResponse({'code': 'unauthorized'}, 401)(scope, receive, send)
        body = bytearray()
        while True:
            message = await receive()
            if message['type'] == 'http.disconnect':
                return
            body.extend(message.get('body', b''))
            if len(body) > MAX_BODY:
                return await JSONResponse({'code': 'request-too-large'}, 413)(scope, receive, send)
            if not message.get('more_body', False):
                break
        sent = False
        async def bounded_receive():
            nonlocal sent
            if not sent:
                sent = True
                return {'type': 'http.request', 'body': bytes(body), 'more_body': False}
            return await receive()
        await self.inner(scope, bounded_receive, send)


app.add_middleware(PrivateBodyBoundary)


@app.exception_handler(RequestValidationError)
async def invalid_request(_request, _error):
    # FastAPI's default validation response echoes submitted text/audio.
    return JSONResponse({'code': 'invalid-language-request'}, status_code=422)


@app.get('/health/live')
def live():
    return {'status': 'ok'}


@app.get('/health/ready')
def ready():
    if not os.environ.get('ELLAI_LANGUAGE_INTERNAL_KEY') or not getattr(getattr(app.state, 'engine', None), 'verified', False):
        raise HTTPException(503, 'provider-unavailable')
    return {'status': 'ready', 'quality': 'unreviewed'}


def execute(operation, request):
    try:
        engine = getattr(app.state, 'engine', None)
        if engine is None:
            raise HTTPException(503, 'provider-unavailable')
        result = getattr(engine, operation)(request)
        return JSONResponse(result, headers={'Cache-Control': 'no-store'})
    except ModelBusy:
        raise HTTPException(429, 'provider-busy', headers={'Retry-After': '1'}) from None
    except (RuntimeError, OSError, ValueError):
        raise HTTPException(503, 'provider-unavailable') from None


@app.post('/v1/translate')
def translate(request: TranslationRequest):
    return execute('translate', request)


@app.post('/v1/transcribe')
def transcribe(request: TranscriptionRequest):
    return execute('transcribe', request)
