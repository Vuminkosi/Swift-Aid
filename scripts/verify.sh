#!/usr/bin/env sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

python -m unittest discover -s tests/bootstrap -v
test -f SwiftAid.sln || { echo "SwiftAid.sln is missing" >&2; exit 1; }

dotnet restore SwiftAid.sln --locked-mode
dotnet test SwiftAid.sln --no-restore

command -v uv >/dev/null 2>&1 || { echo "uv is required for the locked Python worker check" >&2; exit 1; }
test -f workers/uv.lock || { echo "workers/uv.lock is missing; create the Python 3.12 lock before verification" >&2; exit 1; }
uv sync --project workers --locked --all-groups
uv run --project workers --locked ruff check workers/src workers/tests
uv run --project workers --locked pytest workers/tests -q

# Validate Compose with disposable process-local placeholders; no services are started.
POSTGRES_DB=swiftaid_config_check \
POSTGRES_USER=config_check \
POSTGRES_PASSWORD=not-a-production-secret \
QDRANT_API_KEY=not-a-production-secret \
NATS_GATEWAY_USER=gateway_check \
NATS_GATEWAY_PASSWORD=not-a-production-secret \
NATS_WORKER_USER=worker_check \
NATS_WORKER_PASSWORD=not-a-production-secret \
  docker compose -f infra/compose.yml config --quiet
