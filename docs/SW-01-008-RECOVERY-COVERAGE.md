# SW-01-008 recovery test coverage audit - 2 November 2026

## Outcome

Existing source tests cover selected deterministic paths, but this checklist item remains open because no single recovery matrix covers all listed failures and the current runtime restart check failed. No test was run for this read-only inventory.

| Failure case | Existing evidence | Current result |
|---|---|---|
| Duplicate/replayed command | `tests/SwiftAid.Integration.Tests/CaseCommandProcessingPostgresTests.cs`; command receipt tests; `tests/SwiftAid.Domain.Tests/CaseAggregateTests.cs` | Managed case receipt/replay and deterministic event replay have focused test source. Latest test evidence remains the previously recorded suites, not a new SW-01-008 run. |
| Out-of-order event/schema rejection | `CaseAggregateTests.cs`; `workers/tests/integration/test_job_delivery.py`; version checks in command/job contracts | Unit/contract test coverage exists for selected paths. Full cross-language stale-schema and out-of-order broker recovery remains unverified. |
| NATS redelivery/worker recovery | `workers/tests/classification_live.py`; `tests/SwiftAid.Integration.Tests/IngestionRestartTests.cs` | Live classification test requires broker/credentials; current live service readiness did not publish/consume. No general worker restart/redelivery proof. |
| Outbox publish failure/restart | `IngestionOutboxPostgresTests.cs`, `IngestionRestartTests.cs`, `JobPublisherContractTests.cs` | Focused integration test sources exist for ingestion/outbox/publisher contracts; current evidence does not establish all lanes' persisted retry/recovery semantics. |
| Service restart | [SW-01-002 runtime record](SW-01-002-RUNTIME.md) | Gateway restart command failed at optional worker namespace recovery (BL-009); data services were not restarted. |

## Closure work

Add/maintain a fault matrix that names each lane, its deterministic unit/contract test, disposable integration environment, expected persisted state after duplicate/out-of-order/stale-schema/publish-failure events, and restart recovery command. Run live NATS/PostgreSQL/service scenarios only in their approved disposable environment; preserve external-provider, Android and browser boundaries as separate evidence.

No tests were executed and no service or data was changed for this audit. See SW-01-006 for broader durable inbox/lease/ACK coverage gaps.
