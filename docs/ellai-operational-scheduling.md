# Ellai operational scheduling

The schedule store is a disabled-by-default coordination foundation. No rows are
created automatically and no worker or specialist is started by this change.
It accepts only the six operational role IDs and four- or eight-hour cadences.
Workspace and role form the schedule identity; policy version is captured in
each lease. A database-clock lease, unique lease ID and increasing fencing token
prevent a stale claimant from completing, renewing or abandoning a later claim.
Renewal cannot extend a work attempt beyond ten minutes. Failed work may abandon
the lease without advancing the due cursor; successful completion advances from
the previous due time past missed intervals rather than replaying every missed
sweep. The migration refuses rollback while schedule rows exist.

The intended starting cadences from the design are four hours for enrichment,
quality and follow-up; eight hours for organization, correlation and spatial
analysis. These are not enabled defaults. Authorized workspace provisioning,
role-specific batch capacities, jitter, retry limits, worker identity, case
authorization, work-turn sessions, telemetry, model-cost ceilings and a worker
that connects the fence to atomic domain changes remain to be implemented.
This lease alone does not authorize access to cases or fence downstream writes.

Disposable PostgreSQL coverage exercises concurrent claims, workspace scoping,
expiry and replacement, stale-fence refusal, renewal deadline, abandon/reclaim,
four- and eight-hour cursor advancement and rollback refusal. Production role
grants, deployment, long-running workers and cross-replica end-to-end behavior
remain unverified.
