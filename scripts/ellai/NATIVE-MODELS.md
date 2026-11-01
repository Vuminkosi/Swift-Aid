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
