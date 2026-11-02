# SW-01-002 live service checks — 2 November 2026

## Environment and procedure

Read-only checks ran against the existing local `swiftaid` Compose project. The existing Gateway container image was not rebuilt, so source-to-image equivalence is NOT VERIFIED. No database writes, migrations, provider inference, model provisioning, or other service restarts were performed.

Before the restart check, `docker compose -f infra/compose.yml ps --format table` showed Gateway, PostgreSQL, MinIO, NATS, Redis, ellai-language and ellai-tts healthy; Qdrant was running without a Docker health status. The optional classification worker was not running in that view.

## Connection/readiness checks

| Command / target | Result | Scope |
|---|---|---|
| `curl.exe --fail --silent --show-error --output NUL --write-out "%{http_code}" http://127.0.0.1:8080/health/live` | PASS, HTTP 200 before and after restart | Gateway liveness only. Gateway `Program.cs` maps `/health/live`; no Gateway readiness endpoint was found. |
| `docker compose -f infra/compose.yml exec -T postgres sh -c 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"'` | PASS, accepting connections before and after restart | PostgreSQL listener readiness; no application query or migration was run. |
| `curl.exe --fail --silent --show-error --output NUL --write-out "%{http_code}" http://127.0.0.1:9010/minio/health/ready` | PASS, HTTP 200 before and after restart | MinIO readiness endpoint; no bucket/object operation was performed. |
| `docker compose -f infra/compose.yml exec -T nats sh -c 'wget -q -O - http://127.0.0.1:8222/healthz'` | PASS, `{"status":"ok"}` | NATS HTTP health endpoint; no publish/consume or JetStream workflow was run. |
| `docker compose -f infra/compose.yml exec -T redis sh -c 'REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli ping'` | PASS, `PONG` before and after restart | Redis ping only; no cache data was read or changed. |
| Gateway container `curl` to `http://ellai-language:8091/health/ready` | PASS, HTTP 200 before and after restart | Gateway-to-language-service network reachability and readiness endpoint; no translation/inference call. |
| Gateway container `curl` to `http://ellai-tts:8090/health/ready` | PASS, HTTP 200 before and after restart | Gateway-to-TTS network reachability and readiness endpoint; no audio synthesis call. |
| Gateway container `curl` to `http://qdrant:6333/readyz` | PASS, HTTP 200 before and after restart | Gateway-to-Qdrant network reachability and readiness endpoint; no collection, write, or search operation. |

These checks do not establish Meta, Firebase, or other external provider readiness, model capability, application-level connection configuration, data persistence, or end-user workflows.

## Restart check and result

Command: `docker compose -f infra/compose.yml restart gateway`

Result: FAIL as a Compose operation (exit 1). Docker reported that Gateway restarted and became healthy, then attempted to restart `classification-worker`, which uses `network_mode: service:gateway`; it could not join the recorded Gateway network namespace because that container ID did not exist. A subsequent `docker compose -f infra/compose.yml ps --all --format table` showed the classification worker already exited with code 255 (its displayed exit was about 10 hours old). It remains stopped; it was not recreated or retried. The Gateway and all readiness endpoints listed above were healthy after the operation.

## Status

`SW-01-002` is **Partial / Blocked**: local service/readiness endpoints and Gateway-to-provider-service network reachability passed, but the Compose dependency restart did not complete and no external-provider or end-to-end workflow was exercised. Follow up under `BL-009` in `docs/BLOCKERS.md`. No claims are made about a general service recovery or restart guarantee.

## Refreshed read-only readiness snapshot - 3 November 2026

Command `docker compose -f infra/compose.yml ps --all` showed Gateway, PostgreSQL, MinIO, NATS, Redis, Ellai language and Ellai TTS healthy/up. Qdrant was up without a Compose health status. The classification worker remained exited (255); bootstrap and migration containers were exited successfully as one-shot jobs.

PASS: Gateway `GET /health/live` returned 200; PostgreSQL `pg_isready` accepted connections; MinIO `/minio/health/ready` returned 200; NATS `/healthz` returned `{"status":"ok"}`; Redis `PING` returned `PONG`; Gateway-to-language `/health/ready`, Gateway-to-TTS `/health/ready`, and Gateway-to-Qdrant `/readyz` each returned HTTP 200. These are listener/readiness/network checks only. No migration, application database query, object read/write, JetStream publish/consume, Qdrant collection operation, model inference, external provider call or service restart was performed. The earlier Compose restart failure remains unresolved. Source-to-running-image equivalence is NOT VERIFIED.
