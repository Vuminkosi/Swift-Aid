# Canonical command receipt hash protocol

## Scope and status

This document defines the internal canonical bytes used to hash a supported case command for receipt identity. It is a private receipt-hash protocol. It is **not** the external command wire JSON, a public API envelope, or the serialized command sent by a client.

Protocol identifier: **swiftaid.case-command-receipt**
Receipt protocol version: **1**
Supported wire-command schema version: **1**

These are distinct version fields. The receipt protocol version identifies the canonical byte format and is stored in the receipt's **SchemaVersion** (**CommandReceiptEntity.SchemaVersion** in the current persistence model). The wire-command schema version describes the supported command schema and is included separately in the canonical bytes. The current source implementation is present in **CanonicalCaseCommandHasher**; use of this hasher by a receipt writer, persistence pipeline, replay path, or production route is **NOT VERIFIED**.

## Canonical object and field order

Serialize one compact JSON object as literal UTF-8 without a BOM or trailing newline. Property order is normative and must not be changed within receipt protocol version 1:

1. **protocolId** — exact protocol identifier string.
2. **receiptProtocolVersion** — integer 1.
3. **wireCommandSchemaVersion** — integer 1.
4. **actorId** — exact validated actor identifier string.
5. **caseId** — hyphenated GUID in D format.
6. **commandId** — hyphenated GUID in D format. This business input is included; generated receipt, event, or database-row identifiers are excluded.
7. **commandType** — the exact closed command discriminator listed below.
8. **expectedVersion** — the expected aggregate version integer.
9. **occurredAtUtc** — explicit JSON null, or the command timestamp converted to UTC and formatted with invariant O format. The UTC value uses offset +00:00.
10. **reason** — the exact validated reason string.
11. **payload** — a JSON object containing every remaining business input for that command, in the command-specific field order below.

All strings preserve their exact validated values: do not trim, case-fold, normalize Unicode, reorder lists, or otherwise canonicalize application text. The JSON writer uses an explicit relaxed encoder so Unicode is emitted literally as UTF-8; JSON-required escaping for quotes and control characters still applies. Emit explicit null values where the schema calls for nullable fields. Do not omit null properties.

The hasher computes SHA-256 over the exact serialized UTF-8 bytes. Hashes are opaque 32-byte values; do not hash a parsed/reformatted object, a pretty-printed representation, or the external wire envelope.

## Supported commands and payload fields

Only these non-creation command types are supported in protocol version 1. Unknown command types and unsupported protocol/schema versions fail closed. Case creation (AcceptCaseCommand) is not supported by this non-creation receipt protocol.

| commandType | payload fields in order |
|---|---|
| CorrectReport | correctedNarrative |
| RequestCaseAction | requestId, action, evidenceEventIds (preserve input order), proposedOwnerId (explicit null when absent) |
| ReviewCaseActionRequest | requestId, outcome, acceptedOwnerId (explicit null when absent) |
| ResolveCase | Empty object: {} |
| ReopenCase | accountableOwnerId |

requestId uses GUID D format. Enum values use their exact supported names. Payload field order is part of the protocol; do not alphabetize fields or evidence identifiers.

The canonical request includes all business inputs represented by the ordered fields and payload. It excludes server-recorded time (RecordedAtUtc), access epoch, roles/claims, and generated receipt/event/database identifiers. Execution must use the immutable command snapshot returned with the canonical hash, not the caller's original mutable object. The current implementation snapshots evidence identifiers before hashing and execution.

## Golden vector

contracts/fixtures/case-command-receipts/v1-review-pending.json is the normative protocol-v1 golden byte vector. It is 475 UTF-8 bytes, has no BOM, and has no trailing newline. Its SHA-256 is 888cecb1cafde04a55fe7e40df1a543ac1040062d4500f89f9808e639eba1b83; the .sha256 sidecar has SHA-256 9434866564935b5a6f1d205a018d4980706145874119b72bbc0e9f17b54bb391. The vector exercises literal Unicode, explicit nulls, evidence order, and UTC normalization:

    {"protocolId":"swiftaid.case-command-receipt","receiptProtocolVersion":1,"wireCommandSchemaVersion":1,"actorId":"worker-ü","caseId":"14aa194b-15ca-42fe-857b-465fcd0e2f33","commandId":"b08c5c48-3a85-4bc5-9f92-222ac4abf9b4","commandType":"ReviewCaseActionRequest","expectedVersion":3,"occurredAtUtc":"2026-10-27T12:00:00.0000000+00:00","reason":"révision — inconnue","payload":{"requestId":"12f3749d-b55b-46be-b961-821e57203489","outcome":"Pending","acceptedOwnerId":null}}

The fixture tests compare the hasher output to this exact vector. The implementation formats timestamps with invariant O formatting and the focused test explicitly exercises a tr-TR current culture against the same expected bytes.

## Versioning and compatibility

Any change to byte encoding, field names/order, null treatment, payload shape/order, string handling, GUID/timestamp formatting, or included business inputs changes the receipt hash protocol. Such a change requires a new receiptProtocolVersion; do not silently revise version 1. Keep the supported wire-command schema version separate. Unknown stored receipt protocol versions fail closed; do not reinterpret an unknown hash under the current rules.

## Evidence and integration boundary

The focused hasher suite passed 10/10 with .NET SDK 10.0.403 using:

    dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~CanonicalCaseCommandHasherTests --verbosity minimal

Independent code/API/security reviews passed for the reviewed source; the security review was based on supplied excerpts and did not verify a persistence or replay caller. Source evidence hashes:

| File | SHA-256 |
|---|---|
| src/SwiftAid.Application/Cases/Commands/CanonicalCaseCommandHasher.cs | eb4bbc5b5c2f19e2a7b99e7182c28b50134be75e02b0ca8bb0ca2048933bdebd |
| tests/SwiftAid.Application.Tests/CanonicalCaseCommandHasherTests.cs | 9102a1c8803bef17027fb0b23855f7e4c972e80faee8f4daee18042a1e0280a0 |
| contracts/fixtures/case-command-receipts/v1-review-pending.json | 888cecb1cafde04a55fe7e40df1a543ac1040062d4500f89f9808e639eba1b83 |
| contracts/fixtures/case-command-receipts/v1-review-pending.sha256 | 9434866564935b5a6f1d205a018d4980706145874119b72bbc0e9f17b54bb391 |

The related P2.1B1 policy/API source commit is 9aed5780ff77ff140d8ff15df9b0b4738580161e; the focused B1 suite passed 35/35 before commit and again at HEAD `9aed5780ff77ff140d8ff15df9b0b4738580161e` with .NET SDK 10.0.403 using `dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AuthorizationPolicyTests|FullyQualifiedName~CaseCommandAuthorizationMapTests" --verbosity minimal` (exit 0, 0 failed/skipped, 112 ms). The receipt hasher, test, golden-vector, and sidecar files remain uncommitted and are not included in commit `9aed5780ff77ff140d8ff15df9b0b4738580161e`. Receipt creation, persisted SchemaVersion/hash behavior, duplicate/replay handling, SQL writer integration, and production route/transaction behavior remain NOT VERIFIED.
