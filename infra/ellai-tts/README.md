# Shared MMS speech worker

English retains its existing voice ID, pinned checkpoint and request contract. Native profiles are opt-in; absent profiles return 422 rather than substituting English. No request downloads a model.

`ELLAI_TTS_NATIVE_PROFILES` is a JSON array of up to three entries. Each entry supplies `voiceId`, `language` (`sna`, `nya`, or `tso`), `modelId` (`facebook/mms-tts-<language>`), a 40-character lowercase commit `revision`, a 64-character `sha256` of `model.safetensors`, and an absolute local `directory`. Supply validated model files through read-only mounts. Do not use branch names as revisions. Native requests specify both `voiceId` and `language`; both must match the configured profile. Omitting language retains English compatibility.

`ELLAI_TTS_MAX_RESIDENT_MODELS` defaults to 2 and accepts 1–4. Least-recently-used models are evicted before another model loads. One lock owns model loading and inference, preventing duplicate loads, unsafe eviction and overlapping inference. Concurrent requests receive 429 with `Retry-After: 1`; callers retain responsibility for cancellation and bounded retries. Failed loads release the lock and return 503. Returned metadata identifies the selected checkpoint, revision, language and actual sample rate.

The cache bound limits model count, not bytes or VRAM. Inference currently remains on CPU. An in-flight synchronous model call cannot be interrupted by client disconnect; its output is still bounded. English readiness does not certify native profiles or clinical-language quality. The configured revision is operator-supplied provenance; the model weights are hash-checked, while full tokenizer artifact attestation remains outstanding.

Run `.venv/Scripts/python.exe -m pytest -q -p no:cacheprovider` on Windows. Tests use synthetic models for routing, PCM, authorization, cache, eviction, failure and contention behavior. Actual native checkpoint inference and Gateway/client language routing are separate acceptance gates and are not established by these tests.

Ellai's default speaking rate is **0.92**, applied through the installed MMS/VITS model's native `speaking_rate` parameter during synthesis. PCM remains 16 kHz for the English checkpoint; clients do not slow playback or resample audio. The response identifies this setting with `X-SwiftAid-Speaking-Rate`. No extra punctuation pauses, trailing silence, or other output processing is applied.

2026-11-02 verification: the new parameter regression failed before implementation and passed afterward; `tests/test_app.py` plus `tests/test_voice_pool.py` passed 10 tests and four subtests. The real rebuilt Compose MMS-TTS worker returned HTTP 200, rate 0.92, 16 kHz, and 97,280 PCM bytes for a synthetic sentence in 714 ms. This checks actual inference and metadata, not subjective voice quality. The existing torch deprecation and Compose model-volume provenance warnings remain. No model was replaced and no mobile tests ran.
