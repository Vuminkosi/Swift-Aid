# SW-05-007 follow-up audit

Date: 2 November 2026
Branch: `interactive-ellai`
Audited revision: `7260a5f` plus the current durable reminder implementation slice (2 November 2026)

## Result

**PARTIAL; checklist remains unchecked.** Existing scheduling, outcomes, queues and follow-up acknowledgement are implemented. Durable generic reminder occurrence creation, idempotency and current-authority suppression now pass focused unit and disposable PostgreSQL tests. Eligible-assignee validation, case-fact reuse, bounded factual clarification, chained browser-to-persistent-store and physical-device acceptance remain open.

## Evidence by requirement

| Requirement | Current evidence | Result |
|---|---|---|
| SA-FUP-01 schedule with purpose, assignee and human due instant | `ScheduleFollowUpCommand`, `CaseFollowUpScheduled`, work-management form; PostgreSQL projection stores purpose, assignee, due time. UI exposes local date/time and timezone handling. | Partial: verify eligible-assignee enforcement and unchanged case-context reuse end to end. |
| SA-FUP-02 acknowledgement, completion, missed/unreachable, reschedule, cancel with history | `FollowUpStatus` contains Scheduled/Acknowledged/Completed/Missed/Unreachable/Cancelled. `AcknowledgeFollowUpCommand` and `CaseFollowUpAcknowledged` enforce the assigned worker and unresolved case. Outcome command requires notes and reason; reschedule requires a different due time and records previous/new due. Event journal retains prior outcome. | Partial: acknowledgement and durable reminder suppression are verified; chained browser journey remains open. |
| SA-FUP-03 completion has documented outcome | Domain requires non-empty `OutcomeNotes` for every outcome, including Completed. | Source/domain covered; linked observation is not required, which the specification permits because a documented outcome is sufficient. |
| SA-FUP-04 no-contact is not completion | Missed and Unreachable are distinct statuses and can only transition to Rescheduled; no-contact cannot be recorded as Completed from those states. | Domain behavior covered; verify rendered/persistent journey. |
| SA-FUP-05 preserve old/new due, actor, reason | `CaseFollowUpOutcomeRecorded` records actor, previous due, new due, reason; replay changes current due while journal retains original. | Source/domain covered; persistent integration and rendered history are not verified here. |
| SA-FUP-06 assigned and authorized regional due/overdue queue | PostgreSQL queue queries scheduled work by assignee or region candidate; `CaseMonitoringQueueQuery` applies per-case ReadCase authorization and computes overdue from current time. | Source exists; current acceptance-run evidence must be recorded for PostgreSQL and rendered queue. |
| SA-FUP-07 reuse unchanged subject/species/demographics | Follow-up is linked to its case and current work form acts on existing work. | Not verified: no acceptance evidence demonstrates the routine path avoids re-entry. |
| SA-FUP-08 up to three factual/admin questions, no clinical prompts | No follow-up question flow was found in the audited UI path. | Not implemented/verified; do not add clinical question generation. |
| SA-FUP-09 reminder suppression after terminal/reschedule | Durable due dispatcher creates a generic notification and transactional delivery-outbox row keyed by follow-up/current-schedule version. Inbox and background authorization recheck current status, assignee, version and `ManageFollowUp`; reschedule and terminal state suppress stale occurrences. | Focused unit and disposable PostgreSQL verification PASS; device/provider delivery is not claimed. |
| SA-FUP-10 no AI-generated clinical interval | UI asks staff for due date/time; no disease-derived interval was found in the audited scheduling path. | Source boundary present; runtime/model non-generation acceptance remains unverified. |

## Checks run

- PASS — `dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --verbosity quiet --filter FullyQualifiedName~MonitoringWorkflowTests` — 10 passed, 0 failed, 0 skipped. Covers domain scheduling/outcome transitions, required notes, rescheduling discipline, and missed/unreachable handling; does not prove persistence, UI, reminders, or device behavior.
- PASS — source audit of `spec.md` §11, `implementation.md` follow-up state contract, aggregate, monitoring queue, PostgreSQL projection, work-management UI and relevant domain tests.
- NOT VERIFIED — follow-up acknowledgement, follow-up reminders/suppression, full PostgreSQL lifecycle acceptance, rendered browser journey, Android device workflow, eligible-assignee validation, case-fact reuse, and factual clarification question limit.

## Next implementation work

Close the remaining assignee eligibility, case-fact reuse, bounded factual clarification, chained persistence and device acceptance evidence. Keep staff-set due times authoritative; never derive a clinical interval. Do not mark SW-05-007 complete until these and the required end-to-end evidence are closed or explicitly classified by the master checklist gate.

## Acknowledgement implementation update — 2 November 2026

The acknowledgement slice is implemented. Migration `20261103130000_FollowUpAcknowledgement` extends status and event constraints while retaining existing assistance event types.

- PASS — Domain: 104 passed.
- PASS — Application: 895 passed, 3 skipped.
- PASS — Client: 326 passed.
- PASS — UI: 542 passed.
- PASS — Integration command/journal filter: 59 passed.
- PASS — Worker schema: 4 passed.
- PASS — synthetic Chromium at 390px and 1280px, including acknowledgement.
- PASS — Android Release build: 0 warnings, 0 errors.
- PASS — EF pending-model check reports no changes; EF tools 10.0.12/runtime 10.0.14 warning.
- PASS — prior focused disposable PostgreSQL acknowledgement persistence/authorized-queue test: 1/1; monitoring regression: 26/26. These database tests were not repeated in this verification pass.

At the acknowledgement checkpoint, reminder creation/suppression was still open. The durable reminder update below supersedes that part; eligible-assignee proof, unchanged case-fact reuse, factual clarification limited to three questions, chained browser-to-PostgreSQL journey, physical Android interaction and deployed provider behavior remain open. SW-05-007 remains unchecked.

## Durable reminder implementation update — 2 November 2026

Follow-up reminders now read due open rows from the PostgreSQL follow-up projection and create a generic `CaseNeedsAttention` notification plus the existing transactional delivery-outbox row. The occurrence key is stable per follow-up and current schedule version. The notification stores an opaque follow-up ID/version internally; it exposes no purpose or case facts. Inbox and background delivery authorization recheck current follow-up status, assignee, schedule version and `ManageFollowUp` permission. Completion, cancellation, reschedule, actor inactivity and permission loss suppress stale occurrences. `ScheduledVersion` remains the original schedule version for existing queue behavior; `CurrentScheduleVersion` and `CurrentScheduleEventId` advance on reschedule.

- PASS — `FollowUpReminderDispatcherTests`: 1/1; generic occurrence, stable key, current schedule reference and recipient mapping.
- PASS — disposable PostgreSQL `Follow_up_reminders_are_idempotent_and_stale_occurrences_are_suppressed`: duplicate dispatcher instances produce one occurrence; reschedule hides the old occurrence and creates the new one; completion hides it.
- PASS — `NotificationAuthorizationQueryTests`: 5/5 against the disposable SQLAUTH database.
- PASS — `CaseMonitoringPostgresTests`: 10/10 against disposable PostgreSQL; schedule version/event assertions verify initial and rescheduled projection behavior.
- PASS — fresh disposable PostgreSQL applied all 53 migrations through `20261103140000_FollowUpDueReminders` using Gateway `--migrate-database`.
- PASS — Infrastructure and Gateway build; Integration.Tests build with `-p:WasmBuildNative=false`; EF `has-pending-model-changes` reports none (EF tools 10.0.12/runtime 10.0.14 warning).
- PASS — task-created PostgreSQL container and its tmpfs were removed after verification.

Notification/outbox occurrence creation and current-authority suppression are verified. Provider/device receipt is not claimed. Still open: eligible-assignee validation at schedule time, routine case-fact reuse, bounded factual clarification, chained browser-to-PostgreSQL and physical Android acceptance. SW-05-007 remains unchecked.

## Acknowledgement implementation update — 2 November 2026

The acknowledgement slice is implemented. Migration `20261103130000_FollowUpAcknowledgement` extends status and event constraints while retaining existing assistance event types.

- PASS — Domain: 104 passed.
- PASS — Application: 895 passed, 3 skipped.
- PASS — Client: 326 passed.
- PASS — UI: 542 passed.
- PASS — Integration command/journal filter: 59 passed.
- PASS — Worker schema: 4 passed.
- PASS — synthetic Chromium at 390px and 1280px, including acknowledgement.
- PASS — Android Release build: 0 warnings, 0 errors.
- PASS — EF pending-model check reports no changes; EF tools 10.0.12/runtime 10.0.14 warning.
- PASS — prior focused disposable PostgreSQL acknowledgement persistence/authorized-queue test: 1/1; monitoring regression: 26/26. These database tests were not repeated in this verification pass.

At the acknowledgement checkpoint, reminder creation/suppression was still open. The durable reminder update below supersedes that part; eligible-assignee proof, unchanged case-fact reuse, factual clarification limited to three questions, chained browser-to-PostgreSQL journey, physical Android interaction and deployed provider behavior remain open. SW-05-007 remains unchecked.
