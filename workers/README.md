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

- `tests/test_nats_acl.py` runs only when `NATS_ACL_TEST_URL`, `NATS_GATEWAY_USER`,
  `NATS_GATEWAY_PASSWORD`, `NATS_WORKER_USER` and `NATS_WORKER_PASSWORD` are set;
  otherwise the two tests skip by design.
- `uv run pytest` runs everything else deterministically without Docker,
  PostgreSQL, NATS, or Meta credentials.
