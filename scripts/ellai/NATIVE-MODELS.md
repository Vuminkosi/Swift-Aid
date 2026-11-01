# Pinned native-language model provisioning

Run from the repository root using the existing speech worker environment:

```powershell
infra/ellai-tts/.venv/Scripts/python.exe scripts/ellai/provision_native_models.py --directory .codex/models
infra/ellai-tts/.venv/Scripts/python.exe scripts/ellai/provision_native_models.py --directory .codex/models --verify-only
infra/ellai-tts/.venv/Scripts/python.exe -B scripts/ellai/test_provision_native_models.py
```

`native-models.lock.json` records immutable Hugging Face revisions, selected artifact sizes, LFS SHA-256 hashes and Git blob hashes for small configuration/tokenizer files. Provisioning validates existing files before reuse, downloads only missing or invalid artifacts, and verifies every downloaded artifact. Interrupted downloads use the Hub client's resumable local cache. Verification fails if any selected artifact is absent or altered. It does not execute model inference.

The selected artifacts total 9,828,449,877 bytes (approximately 9.15 GiB). Provisioning requires an additional 3 GiB free-space reserve. The shared MMS-ASR checkpoint includes only the Shona, Nyanja and Tsonga adapters; unrelated adapters and duplicate weight formats are excluded. NLLB's pinned revision provides PyTorch binary weights rather than safetensors; use a maintained PyTorch runtime with restricted weights loading. No remote model code is required.

The default example directory is Git-ignored. Never commit weights, local cache metadata or generated audio. Mount provisioned model directories read-only into workers. Runtime requests must use local-only loading and must not run this provisioning script. This download approval does not enable a language capability or constitute provider, clinical-language, device, or licensing validation.

Model cards are downloaded and integrity-checked with each checkpoint. Review their research/non-commercial restrictions before any deployment; downloading does not grant additional usage rights. Existing English behavior remains unchanged. Native-language inference, Gateway routing, durable translation provenance, human review and actual client validation remain separate delivery gates.

## Local validation - 2026-11-01

All five model inventories downloaded and passed their pinned artifact checks. Models are retained in `.codex/models`, outside Git. The current Windows environment uses PyTorch 2.14.1+cpu and Transformers 5.22.1; GPU inference was not tested.

- PASS: `python -B scripts/ellai/test_provision_native_models.py`: 2 integrity regression tests.
- PASS: speech worker `python -m pytest -q -p no:cacheprovider`: 9 tests and 4 subtests.
- PASS: local NLLB CPU bfloat16 inference, four CPU threads, synthetic `Good morning.` input, English to Shona, max 24 output tokens: nonempty output in 12.42 seconds including loading. This is one execution smoke check, not a translation benchmark.
- PASS: the pinned tokenizer recognizes `eng_Latn`, `sna_Latn`, `nya_Latn`, `tso_Latn`, and `ssw_Latn`. `nde_Latn` returns the unknown token; Ndebele must not be enabled or substituted.
- PASS: local MMS-ASR CPU bfloat16 loading and sequential Shona/Nyanja/Tsonga adapter switching with one second of synthetic silence: finite logits with vocabulary widths 65/74/33; total 24.70 seconds including loading. This does not measure recognition accuracy.
- PASS: `python -B scripts/ellai/smoke_native_tts.py --directory .codex/models`: actual worker HTTP handler, real weights, capacity-one cache, offline-only loading. All three voices returned nonempty, nonzero 16 kHz PCM and matching language/revision metadata. Shona: 1.031 seconds; Nyanja: 0.781 seconds; Tsonga: 0.750 seconds, each including model loading for a short synthetic greeting. Timings are observations, not latency guarantees.

The TTS smoke uses the real FastAPI route in-process; it does not exercise Docker networking, Gateway routing, English startup readiness, or a client. Native-speaker quality, translation correctness, real speech recognition, clinical meaning preservation, deployed Gateway integration, GPU scheduling, browser and physical-device behavior remain NOT VERIFIED. No live service was reconfigured by provisioning or these checks.
