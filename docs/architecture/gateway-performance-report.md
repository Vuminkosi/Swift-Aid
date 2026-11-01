# Gateway ingestion verification and performance report

2026-11-01. Synthetic, disposable local Docker fixtures on Windows; .NET 10.0.14, PostgreSQL/PostGIS 17, NATS 2.15.1. No patient data.

## Verified dispatch slice

| Check | Result | Observed scope |
|---|---|---|
| Pre-fix outbox model regression | FAIL as expected | Both existing outboxes lacked lease identity/exhaustion state. |
| First real PostgreSQL/NATS run | 7 PASS, 1 FAIL | PubAck crash/replay uncovered duplicate acknowledgement rejection by `EnsureSuccess()`. Failure was corrected, not waived. |
| Corrected focused run | PASS, 25 tests, zero skips, about 2 s test duration | Model, concurrent claims, lease expiry/fencing, bounded retry, expired final attempt, ordered case revisions, exhausted predecessor/requeue, provider failure, real PubAck crash/replay, duplicate/error acknowledgement and existing job publisher contracts. |
| Infrastructure build | PASS | `dotnet build src/SwiftAid.Infrastructure/SwiftAid.Infrastructure.csproj --no-restore -m:1 -nr:false --verbosity quiet`; no warnings/errors. |
| Stream bootstrap with real repository ACL | PASS | Separate disposable broker; bootstrap identity created and verified bounded file stream. No shared broker mutation. |
| Final dispatch regression and Compose validation | PASS | 28 tests, zero skips, about 7 s; includes existing atomic intake attachment-binding tests. `docker compose -f infra/compose.yml --profile ingestion config --quiet` succeeded. |

Test command and environment variables are in [the runbook](gateway-operational-runbook.md). The crash scenario deliberately omits the database completion after a real broker PubAck, advances the fixture lease clock, republishes the same ID and verifies the same broker sequence plus successful durable completion. This is boundary fault injection, not an operating-system crash test.

## Capacity and remaining verification

NOT VERIFIED: 10,000/100,000/1,000,000/multi-million full ingestion and processing runs; sustained throughput, p95/p99 end-to-end latency, CPU/working-set limits, multi-node HA, actual process/network restart, full consumer result-before-ACK, statistical detection accuracy, physical devices and external providers. Stream capacity settings are limits, not measured throughput.

The largest concurrency assertion in the initial functional run is twelve independent intake notices claimed by two database connections without overlap. Test timing is not a throughput benchmark. Additional measured slices will be appended without replacing these limits with estimates.
