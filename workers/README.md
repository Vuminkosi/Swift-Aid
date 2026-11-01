# SwiftAid workers

Gateway-scoped Python background job consumers. Python 3.12 is required
(`requires-python = ">=3.12,<3.13"` in `pyproject.toml`, pinned to `3.12` in
`.python-version`). The project uses [uv](https://docs.astral.sh/uv/) as its only
dependency manager; `uv.lock` is committed and installs are reproducible.

## First-time setup (Windows, macOS, Linux)

1. Install uv with the official installer:
   - Windows (PowerShell): `irm https://astral.sh/uv/install.ps1 | iex`
   - macOS/Linux: `curl -LsSf https://astral.sh/uv/install.sh | sh`
2. Install the pinned interpreter (leaves any other Python installs alone):
   `uv python install 3.12`
3. From the repository root, create the project environment from the lockfile:
   `uv sync --project workers --locked`
   This creates `workers/.venv/` (local only, never committed).

No system-wide `pip install`, no `--break-system-packages`, and no second
environment manager. The host Python version does not matter; `uv run`
always uses the project environment.

## Everyday commands (run from the repository root)

- Tests: `uv run --project workers --locked pytest workers/tests -q`
- Lint: `uv run --project workers --locked ruff check workers/src workers/tests`
- Type check: `uv run --project workers --locked mypy workers/src` (from `workers/`,
  `uv run mypy src` also works)
- Worker version: `uv run --project workers --locked python -m swiftaid_workers --version`

These mirror the `workers` job in `.github/workflows/verify.yml`.

## Integration tests that need live services

## Persistent classification consumer (opt-in)

Install `uv sync --project workers --locked --extra classification`. This adds pinned Torch 2.10.0 and Transformers 4.57.6; ordinary contract tests do not need inference dependencies. The model is `facebook/bart-large-mnli` at revision `d7645e127eaf1aefc7862fd59a17a5aa8558b8ce`, approximately 1.6 GB of safetensors. CPU is the default; `CLASSIFICATION_DEVICE=cuda` requires a compatible CUDA Torch installation and validation on that GPU. Model loading is serialized before consumption. Neither profile has a throughput guarantee.

After the Gateway classification migrations, explicit organization-scoped worker configuration and ingestion stream provisioning, provision the durable using the bootstrap NATS identity:

```powershell
uv run --project workers --locked python -m swiftaid_workers.classification.provision
```

The command reads `NATS_URL`, `NATS_USER`, `NATS_PASSWORD` privately. It creates only `swiftaid_classification_v1` or verifies the existing consumer; it refuses mismatched settings. Existing ingestion streams need an operator-reviewed addition of `swiftaid.ingestion.v1.classification` before rerunning stream bootstrap. Never delete retained work to satisfy bootstrap.

Run `uv run --project workers --locked --extra classification python -m swiftaid_workers --classification` with private `CLASSIFICATION_NATS_URL`, `CLASSIFICATION_NATS_USER`, `CLASSIFICATION_NATS_PASSWORD`, `CLASSIFICATION_GATEWAY_URL`, and `CLASSIFICATION_WORKER_TOKEN`. Gateway `Classification:WorkerTokenSha256` stores only the SHA-256 of that random token; `Classification:Organizations` is an explicit allowlist and `Classification:Enabled` defaults false. Use HTTPS; explicit `CLASSIFICATION_ALLOW_LOOPBACK_HTTP=true` permits only localhost fixtures. The machine credential confers no ordinary user or case-command permissions.

Default source language is `und`: unknown, Shona and siSwati inputs abstain. Set the profile language to English only for a reviewed English source corpus; the current case report contract does not carry a reliable language field, so the worker never guesses it. Semantic scores and the configurable selection threshold are uncalibrated research values, not clinical probabilities or official classifications. Long input abstains instead of silently truncating evidence. Hierarchical pruning is not enabled.

The worker binds a pre-provisioned durable, pulls at most one message per lane (default two), and serializes model execution with token-bounded pair microbatches. ACK follows a committed Gateway outcome. A lost completion response replays the same result; malformed messages require a durable digest-only quarantine record. Source text is not sent through NATS or stored in logs. See [classification architecture and evidence](../docs/architecture/gateway-classification-implementation.md).

The real model integration harness requires `SWIFTAID_RUN_CLASSIFICATION_LIVE=1`, the disposable intake PostgreSQL fixture variables, NATS on loopback port 44229 and the pinned model cached locally. Run the .NET `ClassificationLiveTests` filter; it starts isolated Kestrel and the Python subprocess, checks commit-before-ACK replay/quarantine and publishes lifecycle outbox rows. It does not restart the shared Gateway.

- `tests/test_nats_acl.py` runs only when `NATS_ACL_TEST_URL`, `NATS_GATEWAY_USER`,
  `NATS_GATEWAY_PASSWORD`, `NATS_WORKER_USER` and `NATS_WORKER_PASSWORD` are set;
  otherwise the two tests skip by design.
- `uv run pytest` runs everything else deterministically without Docker,
  PostgreSQL, NATS, or Meta credentials.
