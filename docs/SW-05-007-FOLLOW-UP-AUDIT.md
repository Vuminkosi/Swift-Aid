# SW-05-007 follow-up audit

Date: 2 November 2026
Branch: `interactive-ellai`
Audited revision: `d52de0a`

## Result

**PARTIAL; checklist remains unchecked.** Existing scheduling, outcome, missed/unreachable, rescheduling, cancellation, due-queue, authorization-filtering, and human-entered due-time paths are present. The acceptance contract is not complete: follow-up acknowledgement is specified but has no follow-up-specific event/command/state; there is no follow-up reminder scheduling/suppression path; there is no verified browser-to-persistent-store journey for this workflow. Do not infer these capabilities from assignment acknowledgement, community-session reminders, source-only UI, or a passing unit suite.

## Evidence by requirement

| Requirement | Current evidence | Result |
|---|---|---|
| SA-FUP-01 schedule with purpose, assignee and human due instant | `ScheduleFollowUpCommand`, `CaseFollowUpScheduled`, work-management form; PostgreSQL projection stores purpose, assignee, due time. UI exposes local date/time and timezone handling. | Partial: verify eligible-assignee enforcement and unchanged case-context reuse end to end. |
| SA-FUP-02 acknowledgement, completion, missed/unreachable, reschedule, cancel with history | `FollowUpStatus` contains Scheduled/Completed/Missed/Unreachable/Cancelled. Outcome command requires notes and reason; reschedule requires a different due time and records previous/new due. Event journal retains prior outcome. | Partial: acknowledgement is absent. |
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

Close the acknowledgement contract across domain event/command, serialization, authorization/hash, PostgreSQL projection, API/client/UI and focused tests. Then implement follow-up reminders using the existing reminder/outbox infrastructure with idempotent occurrence keys and suppression on completion/reschedule/cancellation. Keep staff-set due times authoritative; never derive a clinical interval. Do not mark SW-05-007 complete until these and the required end-to-end evidence are closed or explicitly classified by the master checklist gate.
