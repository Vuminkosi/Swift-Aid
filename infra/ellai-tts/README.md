# Shared MMS speech worker

English retains its existing voice ID, pinned checkpoint and request contract. Native profiles are opt-in; absent profiles return 422 rather than substituting English. No request downloads a model.

`ELLAI_TTS_NATIVE_PROFILES` is a JSON array of up to three entries. Each entry supplies `voiceId`, `language` (`sna`, `nya`, or `tso`), `modelId` (`facebook/mms-tts-<language>`), a 40-character lowercase commit `revision`, a 64-character `sha256` of `model.safetensors`, and an absolute local `directory`. Supply validated model files through read-only mounts. Do not use branch names as revisions. Native requests specify both `voiceId` and `language`; both must match the configured profile. Omitting language retains English compatibility.

`ELLAI_TTS_MAX_RESIDENT_MODELS` defaults to 2 and accepts 1–4. Least-recently-used models are evicted before another model loads. One lock owns model loading and inference, preventing duplicate loads, unsafe eviction and overlapping inference. Concurrent requests receive 429 with `Retry-After: 1`; callers retain responsibility for cancellation and bounded retries. Failed loads release the lock and return 503. Returned metadata identifies the selected checkpoint, revision, language and actual sample rate.

The cache bound limits model count, not bytes or VRAM. Inference currently remains on CPU. An in-flight synchronous model call cannot be interrupted by client disconnect; its output is still bounded. English readiness does not certify native profiles or clinical-language quality. The configured revision is operator-supplied provenance; the model weights are hash-checked, while full tokenizer artifact attestation remains outstanding.

Run `.venv/Scripts/python.exe -m pytest -q -p no:cacheprovider` on Windows. Tests use synthetic models for routing, PCM, authorization, cache, eviction, failure and contention behavior. Actual native checkpoint inference and Gateway/client language routing are separate acceptance gates and are not established by these tests.

Ellai's default speaking rate is **0.95**, applied through the installed MMS/VITS model's native `speaking_rate` parameter during synthesis. PCM remains 16 kHz for the English checkpoint; clients do not slow playback or resample audio. The response identifies this setting with `X-SwiftAid-Speaking-Rate`. No extra punctuation pauses or trailing silence are applied. Optional clarity processing is described below.

2026-11-02 verification: the new parameter regression failed before implementation and passed afterward; `tests/test_app.py` plus `tests/test_voice_pool.py` passed 10 tests and four subtests. The real rebuilt Compose MMS-TTS worker returned HTTP 200, rate 0.92, 16 kHz, and 97,280 PCM bytes for a synthetic sentence in 714 ms. This checks actual inference and metadata, not subjective voice quality. The existing torch deprecation and Compose model-volume provenance warnings remain. No model was replaced and no mobile tests ran.


2026-11-02 pace adjustment: default native MMS speaking rate is now **0.95**. The worker was rebuilt and recreated through `infra/compose.yml`, preserving the existing internal authentication key and model volume. Real local inference returned HTTP 200, rate 0.95, 16 kHz, 104,448 PCM bytes in 626 ms for a synthetic sentence. Worker health is healthy. The full worker suite passed 18 tests and four subtests; the updated synthesis suite then passed six tests, including a punctuation-containing input whose output remains exactly the model's three synthetic samples. No punctuation/trailing PCM padding is introduced. Model-generated natural pauses remain; this is not a claim that MMS output has no silence. Existing torch deprecation and model-volume provenance warnings remain unchanged.
# Optional synthesis clarity processing

Processing is off by default. Set `ELLAI_TTS_CLARITY_PROFILES` to a JSON language map,
for example `{"eng":{"enabled":true},"sna":{"enabled":false}}`. Each profile accepts
`high_pass_hz` (80), `low_shelf_hz` (180), `low_shelf_db` (-3), `presence_hz` (3000),
`presence_db` (1.5), and `ceiling_db` (-1). The internal authenticated synthesis
request accepts `bypassProcessing: true` for an unprocessed comparison. No client EQ
is added. Speaking rate remains 0.95; sample rate, duration and sample count remain unchanged.

The worker applies a second-order Butterworth high-pass, shelf slope 1 and presence
Q 1 using float64 SciPy SOS filtering once to the model waveform before s16le encoding.
Coefficients follow the [W3C Audio EQ Cookbook](https://www.w3.org/TR/audio-eq-cookbook/).
Filter frequencies must be below Nyquist. A constant attenuation caps the sample
peak at -1 dBFS without upward normalization or limiter pumping. This is a digital
sample-peak ceiling, not a true-peak certification. No silence is appended and no
punctuation pauses are inserted. Invalid/nonfinite waveforms are rejected.

Source audit: web uses AudioBuffer at the received sample rate; Android AudioTrack
and iOS AVAudioFormat likewise use the supplied rate. None applies another EQ or
playback-speed change. Native voice-communication routing remains unchanged and may
affect the physical output. Microphone enhancements are not synthesis filters.

Verification: `python -m pytest infra/ellai-tts/tests -q -p no:cacheprovider` passed
27 tests and 4 subtests; `node --test tests/voice/ellai-audio-playback.test.mjs` passed
4 tests. Filter response was checked at 8, 16, 22.05 and 48 kHz. Real listening,
same-waveform model A/B, deployed latency and mobile playback remain NOT VERIFIED.
Do not enable the profile by default until those checks are assessed.

## Kokoro English adapter (integration in progress)

`ELLAI_TTS_ENGLISH_PROVIDER=kokoro` selects `af_heart` at speed 0.95, loaded from
`ELLAI_KOKORO_MODEL_PATH` (default `/models/kokoro`). The model, configuration and
Heart voice files are SHA-256 checked before loading. Only English uses this
adapter; existing `ELLAI_TTS_NATIVE_PROFILES` continue selecting their pinned MMS
checkpoints. Omitted or `default` voice IDs resolve within the requested language;
a language/voice mismatch returns 422. English Kokoro PCM is mono s16le at 24 kHz.
MMS clarity processing is never applied to Kokoro.

The optional `kokoro` dependency group is locked separately. `Dockerfile.kokoro`
reuses the local MMS runtime image and cached AntHill English linguistic assets;
it installs only missing runtime dependencies. It requires those local images and
does not provision voice weights. The combined image built successfully; a subsequent
build reused it with `--network=none`. Base `Dockerfile` remains MMS-only. Do not select the
Kokoro provider in that image without installing the optional dependencies and
the cached `en_core_web_sm` model.

Real adapter verification on 2 November 2026 used the cached Kokoro 0.9.4 / torch
2.14.1 runtime with Docker `--network none`: 104,400 finite samples, 24 kHz,
4.35 seconds of speech, peak 0.6121, cold load plus synthesis 6.44 seconds. The
retained synthetic listening sample is `.codex/ellai-kokoro-reference.wav`.
This proves local adapter inference, not Gateway delivery, subjective quality or
physical-device playback. Gateway routing now forwards the actual response language;
installed-voice selection and deployment remain outstanding. No new model assets were downloaded.

The combined image also passed real HTTP synthesis with networking disabled:
English Heart returned 94,800 PCM bytes at 24 kHz in 637 ms; MMS Shona returned
39,936 bytes in 2,951 ms; Nyanja 37,888 bytes in 3,105 ms; Tsonga 35,840 bytes in
2,727 ms. Native samples were 16 kHz and included checkpoint loading. Every response
reported rate 0.95. Native test text was only the synthetic product name, not a
language-quality evaluation. Worker tests passed 34 tests and four subtests;
Gateway synthesis/preparation/playback coordination tests passed 28 tests.

Compose now selects Kokoro English by default, mounts the reused local Kokoro
artifacts from `.codex/models/kokoro`, and reads pinned native profiles from
`native-profiles.json` against `.codex/models`. English remains the default request
language. Missing assets produce unavailable speech, never cross-language fallback.
