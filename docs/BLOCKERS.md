# SwiftAid blockers and unresolved verification — 2 November 2026

| ID | Checklist / scope | Status | Evidence and resumption condition |
|---|---|---|---|
| BL-001 | SW-00-002 supplied-document comparison | Blocked | The provided attachment directory contains only the two execution directives; the 16 source documents claimed by the checklist are absent. Resume the comparison when those source documents are present. |
| BL-002 | SW-00-004 full .NET baseline | Failed / partial | `SwiftAid.Ui.Tests` has seven failures (three stale display assertions; four renderer `jsObjectReference` exceptions). The solution run also reported missing iOS `GoogleDataTransport` package assets (`MSB3030`). Repair/restore the local package and diagnose UI failures, then rerun the affected suite and full solution baseline. |
| BL-003 | Browser guest-storage acceptance | Not verified | `node tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` cannot load because `playwright` is absent from the local Node environment. Use an already approved provisioned browser-test environment or add the dependency through the package owner after confirming repository conventions. |
| BL-004 | SW-01-002 live provider readiness | Not verified | Compose lists SwiftAid dependencies running; Qdrant has no reported health status and this audit did not execute provider-specific readiness, restart, persistence or integration workflows. |
| BL-005 | Current task ownership | Unreconciled | `docs/TASK-OWNERS.md` is a historical 27 October snapshot. Refresh from active handoffs before editing shared contracts, migrations, package locks or generated files. |
| BL-006 | SW-00-005 owner decisions PC-01–PC-08 | Unverified | Existing decisions contain unresolved organizer and rights questions; no fresh owner-approved decision evidence was found in the current audit. Keep gated capabilities disabled. |
| BL-007 | SW-06-006 session subscriptions | Missing implementation | The current Gateway registration uses `UnavailableSubscriptionClient`; source search found no subscription API or durable store. Implement on the current session contract and verify login, idempotent subscribe/unsubscribe and reminder suppression before enabling the UI action. |

The unrelated restarting `redis` container is owned by the `core-banking-sim` Compose project, not SwiftAid. It is recorded in `docs/BASELINE-AUDIT.md` as an external runtime observation and is not a SwiftAid service blocker. No foreign service or data was changed.
