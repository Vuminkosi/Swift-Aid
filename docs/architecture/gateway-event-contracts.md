# Gateway ingestion event contracts

## Ingestion notice v1

Stream: `SWIFTAID_INGESTION`. Subjects: `swiftaid.ingestion.v1.intake`, `swiftaid.ingestion.v1.case`.

```json
{
  "schemaVersion": 1,
  "messageId": "16ebbb7d-8a15-4119-aaed-68f740152aa5",
  "sourceKind": "intake",
  "sourceId": "c55843f2-cae9-46fd-883e-1fe529afde81",
  "sourceVersion": 1,
  "reportRevision": 1,
  "eventType": "IntakeReceived",
  "recordedAtUtc": "2026-11-01T05:00:00Z"
}
```

`messageId` is the existing intake outbox ID or managed-case event ID, unchanged across retry and requeue. The same UUID in canonical `D` form is `Nats-Msg-Id`. `sourceId` identifies a private receipt or case; it conveys no permission to retrieve it. For cases, `sourceVersion` is aggregate version and `reportRevision` is the independent facts revision. Intake currently emits the committed first revision only. `recordedAtUtc` is the original outbox timestamp, never retry time. No tenant claim is trusted from a broker notice: Gateway must resolve authoritative ownership before exposing worker input.

This is a new, explicitly named metadata contract. It does not silently replace `CaseEventEnvelope<T>` or the existing `JobEnvelope` contract. Those models retain their callers. Bodies are bounded to 4 KiB. Unsupported versions/invalid identifiers must not be treated as successful work.

## Delivery versus processing

Delivery states are derived from existing rows:

| State | Predicate |
|---|---|
| Pending/retrying | unpublished, not exhausted, no active lease |
| Publishing | unpublished, not exhausted, lease ID and future expiry |
| Published | `published_at_utc` set after expected-stream PubAck |
| Exhausted | `exhausted_at_utc` set; retained failure code and attempt count |

Published means broker storage only. It does not mean extraction, classification, investigation or human verification completed. Consumers still require a durable inbox/result transaction, explicit ACK after commit, current source-version checks and replay reconciliation beyond the two-minute broker deduplication window. These consumer requirements are not satisfied merely by this notice contract.

Failure codes are closed and contain no exception messages: `publish-timeout`, `jetstream-unavailable`, `invalid-notice`, `publish-failed`, `unexpected-stream`, `lease-expired`.
