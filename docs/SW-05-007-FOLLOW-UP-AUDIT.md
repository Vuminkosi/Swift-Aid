# SW-05-007 follow-up audit

Date: 2 November 2026
Branch: `interactive-ellai`
Audited revision: `19ce7ed` plus the current uncommitted acknowledgement slice (2 November 2026)

## Result

**PARTIAL; checklist remains unchecked.** Existing scheduling, outcome, missed/unreachable, rescheduling, cancellation, due-queue, authorization-filtering, and human-entered due-time paths are present. Follow-up acknowledgement is now implemented across command/event/state, authorization/hash, journal, PostgreSQL projection, queue, shared action UI and migration. Reminder scheduling/suppression remains absent, and there is no chained browser-to-persistent-store journey. Do not infer these capabilities from community-session reminders or synthetic UI evidence.

## Evidence by requirement

| Requirement | Current evidence | Result |
|---|---|---|
| SA-FUP-01 schedule with purpose, assignee and human due instant | `ScheduleFollowUpCommand`, `CaseFollowUpScheduled`, work-management form; PostgreSQL projection stores purpose, assignee, due time. UI exposes local date/time and timezone handling. | Partial: verify eligible-assignee enforcement and unchanged case-context reuse end to end. |
| SA-FUP-02 acknowledgement, completion, missed/unreachable, reschedule, cancel with history | `FollowUpStatus` contains Scheduled/Acknowledged/Completed/Missed/Unreachable/Cancelled. `AcknowledgeFollowUpCommand` and `CaseFollowUpAcknowledged` enforce the assigned worker and unresolved case. Outcome command requires notes and reason; reschedule requires a different due time and records previous/new due. Event journal retains prior outcome. | Partial: acknowledgement implementation is verified in domain, application, client, UI, strict journal, PostgreSQL projection and synthetic browser checks; reminder lifecycle and chained journey remain open. |
| SA-FUP-03 completion has documented outcome | Domain requires non-empty `OutcomeNotes` for every outcome, including Completed. | Source/domain covered; linked observation is not required, which the specification permits because a documented outcome is sufficient. |
| SA-FUP-04 no-contact is not completion | Missed and Unreachable are distinct statuses and can only transition to Rescheduled; no-contact cannot be recorded as Completed from those states. | Domain behavior covered; verify rendered/persistent journey. |
| SA-FUP-05 preserve old/new due, actor, reason | `CaseFollowUpOutcomeRecorded` records actor, previous due, new due, reason; replay changes current due while journal retains original. | Source/domain covered; persistent integration and rendered history are not verified here. |
| SA-FUP-06 assigned and authorized regional due/overdue queue | PostgreSQL queue queries scheduled work by assignee or region candidate; `CaseMonitoringQueueQuery` applies per-case ReadCase authorization and computes overdue from current time. | Source exists; current acceptance-run evidence must be recorded for PostgreSQL and rendered queue. |
| SA-FUP-07 reuse unchanged subject/species/demographics | Follow-up is linked to its case and current work form acts on existing work. | Not verified: no acceptance evidence demonstrates the routine path avoids re-entry. |
| SA-FUP-08 up to three factual/admin questions, no clinical prompts | No follow-up question flow was found in the audited UI path. | Not implemented/verified; do not add clinical question generation. |
| SA-FUP-09 reminder suppression after terminal/reschedule | Follow-up projection/queue has no reminder relationship or scheduling hook. Reminder occurrences found belong to community sessions; they are not proof of follow-up reminders. | Not implemented/verified. |
| SA-FUP-10 no AI-generated clinical interval | UI asks staff for due date/time; no disease-derived interval was found in the audited scheduling path. | Source boundary present; runtime/model non-generation acceptance remains unverified. |

## Checks run

- PASS — `dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --no-restore -m:1 -nr:false --verbosity quiet --filter FullyQualifiedName~MonitoringWorkflowTests` — 10 passed, 0 failed, 0 skipped. Covers domain scheduling/outcome transitions, required notes, rescheduling discipline, and missed/unreachable handling; does not prove persistence, UI, reminders, or device behavior.
- PASS — source audit of `spec.md` §11, `implementation.md` follow-up state contract, aggregate, monitoring queue, PostgreSQL projection, work-management UI and relevant domain tests.
- NOT VERIFIED — follow-up acknowledgement, follow-up reminders/suppression, full PostgreSQL lifecycle acceptance, rendered browser journey, Android device workflow, eligible-assignee validation, case-fact reuse, and factual clarification question limit.

## Next implementation work

Implement follow-up reminders using the existing reminder/outbox infrastructure with idempotent occurrence keys and suppression on completion/reschedule/cancellation. Then close the remaining assignee eligibility, case-fact reuse, bounded factual clarification, chained persistence and device acceptance evidence. Keep staff-set due times authoritative; never derive a clinical interval. Do not mark SW-05-007 complete until these and the required end-to-end evidence are closed or explicitly classified by the master checklist gate.

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

Still NOT VERIFIED or absent: reminder creation/suppression, eligible-assignee proof, unchanged case-fact reuse, factual clarification limited to three questions, chained browser-to-PostgreSQL journey, physical Android interaction and deployed provider behavior. SW-05-007 remains unchecked.

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

Still NOT VERIFIED or absent: reminder creation/suppression, eligible-assignee proof, unchanged case-fact reuse, factual clarification limited to three questions, chained browser-to-PostgreSQL journey, physical Android interaction and deployed provider behavior. SW-05-007 remains unchecked.
