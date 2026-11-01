# SwiftAid workers

## Resumable classification backfill

After the additive classification migrations and scoped worker configuration are deployed, start a snapshot with the existing machine credential. Keep the returned job ID; retrying that ID is idempotent. Supply actual authorized workspace/organization IDs.

```powershell
$classificationJob = [guid]::NewGuid().ToString()
$classificationHeaders = @{ Authorization = "Bearer $env:CLASSIFICATION_WORKER_TOKEN" }
$classificationBase = "$($env:CLASSIFICATION_GATEWAY_URL.TrimEnd('/'))/api/internal/classification"
$classificationBody = @{ jobId = $classificationJob; workspaceId = $env:CLASSIFICATION_BACKFILL_WORKSPACE; organizationId = $env:CLASSIFICATION_BACKFILL_ORGANIZATION } | ConvertTo-Json
Invoke-RestMethod "$classificationBase/backfills" -Method Post -Headers $classificationHeaders -ContentType 'application/json' -Body $classificationBody
uv run --project workers --locked --extra classification python -m swiftaid_workers --classification-backfill $classificationJob
Invoke-RestMethod "$classificationBase/backfills/$classificationJob" -Headers $classificationHeaders
```

Use `POST backfills/{id}/control` with `{"paused":true}` to pause or `{"paused":false}` to resume, then rerun the CLI with the same ID. Pausing does not discard an admitted model attempt. `ConfigurationChanged` requires restoring the pinned configuration or creating a new job intentionally. Counters distinguish reuse, new classification, abstention, failure and pending records; a completed job can contain failures. The worker never reimports CSVs. See [durability and verification](../docs/architecture/gateway-classification-implementation.md#durable-historical-backfill).

The backfill CLI uses normal classification settings and transport validation. Historical pacing defaults to one second per item (`CLASSIFICATION_BACKFILL_INTERVAL_SECONDS`, range 0.1–60). Avoid running two model processes on a GPU unless measured capacity supports both. No 15k/million throughput claim follows from this command.

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

Gateway reads declared language from the exact accepted encrypted CSV source row when available. This overrides the corpus fallback; unknown, Shona, siSwati and Ndebele inputs abstain. Default fallback is `und`; set it to English only for a reviewed English source corpus without per-report language metadata. The worker never guesses language. Semantic scores and the configurable selection threshold are uncalibrated research values, not clinical probabilities or official classifications. Long input abstains instead of silently truncating evidence. Hierarchical pruning is not enabled.

The worker binds a pre-provisioned durable, pulls at most one message per lane (default two), and serializes model execution with token-bounded pair microbatches. ACK follows a committed Gateway outcome. A lost completion response replays the same result; malformed messages require a durable digest-only quarantine record. Source text is not sent through NATS or stored in logs. See [classification architecture and evidence](../docs/architecture/gateway-classification-implementation.md).

The real model integration harness requires `SWIFTAID_RUN_CLASSIFICATION_LIVE=1`, the disposable intake PostgreSQL fixture variables, NATS on loopback port 44229 and the pinned model cached locally. Run the .NET `ClassificationLiveTests` filter; it starts isolated Kestrel and the Python subprocess, checks commit-before-ACK replay/quarantine and publishes lifecycle outbox rows. It does not restart the shared Gateway.

- `tests/test_nats_acl.py` runs only when `NATS_ACL_TEST_URL`, `NATS_GATEWAY_USER`,
  `NATS_GATEWAY_PASSWORD`, `NATS_WORKER_USER` and `NATS_WORKER_PASSWORD` are set;
  otherwise the two tests skip by design.
- `uv run pytest` runs everything else deterministically without Docker,
  PostgreSQL, NATS, or Meta credentials.
