# Shared-contract and non-destructive change control — 2 November 2026

## Canonical state observed

- Repository: `C:\DevOps\SwiftAidv2`
- Branch: `interactive-ellai`
- Current revision when recorded: `8f51876bd0e0bf59550ea8029c550c14a55b9064`
- `git worktree list --porcelain`: one worktree, the canonical checkout.
- Worktree was clean after the current documentation commits.

## Rules confirmed

- Keep the active branch and canonical checkout. Do not create/switch/rename/merge/rebase/delete branches or worktrees, force-reset, broadly clean, or push.
- Preserve user changes and stage only the approved task paths. Commit verified, scoped slices on the current branch; inspect staged paths/content first.
- Before shared contracts, migrations, package locks, or generated files are edited, confirm the current path owner from an active handoff. `docs/TASK-OWNERS.md` is a historical snapshot and does not reserve paths or assign current owners.
- Do not deploy, reset shared data, run destructive database tests, or alter unrelated services without explicit scope and a disposable environment where required.
- Keep test fixtures synthetic. Separate source/build proof from live provider, device, browser, deployment, and product acceptance evidence.

## Ownership boundary

The master orchestrator retains checklist, contract and integration coordination. Per-path implementation owners remain unassigned unless current handoffs say otherwise. This process confirmation does not release `BL-005`; shared code work must refresh exact path ownership before editing.

## Prior handoff — P6.3 follow-up reminder slice

- Task owner: primary coordinator / Codex in this session; no competing active handoff or dirty worktree was present at assignment time.
- Objective: implement durable due-time internal reminders for case follow-ups and suppress pending occurrences after terminal status, rescheduling, current-assignee change, or loss of `ManageFollowUp` authorization. Use the staff-selected due instant; do not invent lead time or clinical timing.
- Initial owned paths: `src/SwiftAid.Domain/Notifications/NotificationItem.cs`; `src/SwiftAid.Application/Notifications/NotificationContracts.cs`; `src/SwiftAid.Infrastructure/Persistence/Notifications/`; `src/SwiftAid.Infrastructure/Persistence/Entities/Cases/ManagedCaseFollowUpEntity.cs`; `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs`; `src/SwiftAid.Infrastructure/Authorization/NotificationResourceAuthorizer.cs`; `src/SwiftAid.Infrastructure/Persistence/Notifications/PostgresTrustedNotificationRecipientAuthorizer.cs`; `src/SwiftAid.Gateway/Notifications/NotificationDeliveryWorker.cs`; Gateway DI/hosted service registration; required EF migration/snapshot; focused tests and evidence docs.
- Shared contract and migration ownership is serialized under this handoff. No package lock, external provider, or deployment change is authorized or planned.
- Acceptance: durable due schedules; occurrence-key idempotency across dispatcher instances/restart; exact current follow-up, recipient, status and permission recheck before dispatch; generic notification text; completion/reschedule/cancel/revoke suppression; tests against disposable PostgreSQL where supported.
- Disposition: implementation handoff delivered in prior commits; provider dispatch and user-journey acceptance remain open under BL-007. The listed code and migration paths were released after that implementation slice.

## Completed handoff — SW-01-004 event-type persistence constraint

- Task owner: primary coordinator / Codex in this session; the canonical worktree was clean at handoff creation and no competing migration change was present.
- Objective: align the forward PostgreSQL migration chain's `ck_case_events_type` constraint with the canonical 24-value `CaseEventType` and current EF model; preserve fail-closed behavior for unknown event types.
- Owned paths: `src/SwiftAid.Infrastructure/Persistence/Migrations/` (one forward migration only); `tests/SwiftAid.Integration.Tests/` (focused constraint regression); `docs/SW-01-004-CONTRACT-MAP.md`; `checklist.md`; `docs/PROGRESS.md`; `docs/ACCEPTANCE.md`; this handoff record.
- Invariants: no direct mutation of the shared local database; validate via disposable PostgreSQL only; no removal or rewriting of historical migrations; do not change the event vocabulary or serializer behavior as part of this correction.
- Acceptance: fresh migration chain applies; live constraint admits every canonical event type and rejects an unknown type; focused persistence test passes; source/model/schema and DB constraint names reconcile; SW-01-004 remains partial until its other contract surfaces are mapped.
- Scope limit: no application runtime restart, production migration, destructive operation, remote push, or unrelated event change.
- Disposition (3 November 2026): migration `20261103153000_CaseEventTypeConstraint` and the focused opt-in PostgreSQL test are committed; a task-owned disposable PostgreSQL 17/PostGIS 3.6 database applied the full chain and passed the 24-value/unknown-value assertions. The shared local database remains unchanged. This handoff releases only its listed paths; BL-005 remains for future shared-path edits.
