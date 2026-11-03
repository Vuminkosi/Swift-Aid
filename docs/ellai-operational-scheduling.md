# Ellai operational scheduling

The schedule store is a disabled-by-default coordination foundation. No rows are
created automatically and no worker or specialist is started by this change.
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
role-specific batch capacities, jitter, retry limits, worker identity, case
authorization, work-turn sessions, telemetry, model-cost ceilings and role
handlers remain to be implemented. Trusted database-only handlers may use
the fenced transaction boundary, which rechecks lease expiry before commit and
rolls back no-change work. They must use the supplied transaction and enforce
domain authorization; the schedule lease itself grants no case access and does
not fence external effects.
Each handler must also implement durable operation idempotency and commit any
case activity with its domain change; the transaction boundary does not supply
those records automatically.

Disposable PostgreSQL coverage exercises concurrent claims, workspace scoping,
expiry and replacement, stale-fence refusal, renewal deadline, abandon/reclaim,
four- and eight-hour cursor advancement and rollback refusal. Production role
grants, deployment, long-running workers and cross-replica end-to-end behavior
remain unverified.
