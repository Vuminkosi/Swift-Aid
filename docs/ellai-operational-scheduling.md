# Ellai operational scheduling

The schedule store is a disabled-by-default coordination foundation. The quality
worker creates rows only for explicitly configured and authorized workspaces.
It accepts only the six operational role IDs and four- or eight-hour cadences.
Workspace and role form the schedule identity; policy version is captured in
each lease. A database-clock lease, unique lease ID and increasing fencing token
prevent a stale claimant from completing, renewing or abandoning a later claim.
Due-work discovery requires one trusted workspace and an explicit allowed-role set;
concurrent claimants skip locked schedules and an empty role set grants no work.
Renewal cannot extend a work attempt beyond ten minutes. Failed work may abandon
the lease without advancing the due cursor; successful completion advances from
the previous due time past missed intervals rather than replaying every missed
sweep. The migration refuses rollback while schedule rows exist.

The intended starting cadences from the design are four hours for enrichment,
quality and follow-up; eight hours for organization, correlation and spatial
analysis. These are not enabled defaults. Authorized workspace provisioning,
role-specific handlers beyond quality, retry limits, durable operational metrics,
model-cost ceilings and complete scheduling policy remain to be implemented.
Trusted database-only handlers may use
the fenced transaction boundary, which rechecks lease expiry before commit and
rolls back no-change work. They must use the supplied transaction and enforce
domain authorization; the schedule lease itself grants no case access and does
not fence external effects.
Each handler must also implement durable operation idempotency and commit any
case activity with its domain change; the transaction boundary does not supply
those records automatically.

The first handler, `EllaiQualityWork.RunOneAsync`, assesses one current report
revision in a workspace permitted by the existing classification worker's
organization configuration. It skips an existing assessment before reading
protected evidence, and commits a new assessment and immutable, source-linked
Ellai activity together under the schedule fence. It does not invoke inference,
send messages or create a background conversation. The existing case timeline
shows the activity beneath its source report event; Ellai re-reads the authorized
timeline before replaying an answer so added activity invalidates stale history.
The default-off Gateway quality worker can be enabled through
`Ellai:Operational:Quality` with `Enabled`, `Workspaces` (a bounded explicit
array), and `PolicyVersion`. `BatchSize` defaults to 10 (maximum 100), and
`PollSeconds` defaults to 60 (30-3600). It also requires Ellai framework and
classification to be enabled, with the classification worker credential and
organization scope configured. It checks the active workspace/organization link
before creating a schedule; protected payload access rechecks and locks authority
inside every fenced work transaction. New schedules have a stable 0-299 second
workspace offset, use UTC and run every four hours. Each work turn opens a fresh
in-memory Microsoft Agent Framework session, invokes no model, discards session
context afterward, and emits bounded run outcomes to the configured logs. A
batch that reaches its limit releases the lease without advancing the due cursor;
the next poll resumes it. If another classifier inserts an assessment after
candidate selection, that conflict consumes one bounded turn and the sweep
continues; it is not treated as proof that the workspace has drained. A drained
sweep advances beyond missed intervals. A
policy mismatch releases the lease without changing data or the cursor and needs
operator reconciliation. Changing a schedule policy alone does not re-run an
unchanged quality-rules version; policy change invalidation and affected-stage
reprocessing remain outstanding. Logs need an operator-configured retention
policy; this worker does not create durable run-metrics records. Logs include a
run ID shared with committed activity, role, UTC start and finish, elapsed time,
work-turn and committed-change counts, and outcome or sanitized failure type.
The no-op path records zero changes without creating domain history. Unclaimed
polls are logged at debug level; log retention and visibility depend on the host
logging configuration. No workspace, case, report, or officer identifiers are
included in these log messages.

Disposable PostgreSQL coverage exercises concurrent claims, workspace scoping,
expiry and replacement, stale-fence refusal, renewal deadline, abandon/reclaim,
four- and eight-hour cursor advancement and rollback refusal. Production role
grants, deployment, long-running worker behavior and cross-replica deployment
remain unverified.


## Follow-up review work unit

`EllaiFollowUpWork.RunOneAsync` is a disabled-by-default, database-only work
unit, not yet registered with a hosted scheduler. It requires an `ops.followup`
lease and an explicit bounded organization scope. It records one immutable
recommendation to review an overdue, still-open staff-set follow-up schedule.
The source schedule event, due date, case version, policy, run and work turn are
recorded; a unique follow-up/schedule-version key prevents repeated records.
The workspace and organization are rechecked under the fenced transaction, and
case and follow-up rows are locked while selecting and committing. The activity
is visible on its source event in the existing authorized timeline, and Ellai's
history replay digest changes when the activity appears. A later reschedule or
completion does not erase that historical recommendation. It is not a current
approval, a notification, an officer assignment, a clinical priority, or a new
due date. The new migration refuses rollback when recommendations exist.

The work unit still needs a configured 4-hour hosted worker with one ephemeral
framework session per turn, operational telemetry and a human review workflow.
Production grants, migration deployment, rendered UI and live worker operation
are not verified. No user or officer messages are sent by this work unit.
