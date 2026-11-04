# Ellai health-officer eligibility boundary

The officer lookup is disabled by default (`Ellai:Operational:OfficerDirectory:Enabled`).
It has an authenticated case-scoped candidate endpoint and a separately gated
designation-change endpoint, but no model tool or assignment action. A designation is a controlled organizational role record,
not evidence of professional licensure. The registry is initially empty and its
versioned rows are append-only; a later version supersedes an earlier one. The
migration refuses rollback when records exist.

When enabled and invoked internally, the lookup requires the requesting actor's
current `ReadCase` and `ManageEscalation` authority under a case read lease. A
candidate must have an active, unexpired designation for the case organization,
country and sector, an active actor and membership, and one current grant for
the same organization, workspace, country, sector and sensitivity. That same
grant must permit `ReadCase`, `ManageEscalation`, and `ManageFollowUp`, allow
jurisdictional access, and cover every recorded case region. Cases without a
region yield no candidates. Separate grants are never combined to satisfy the
check. Results contain only the designation identity/version, opaque actor ID,
and an optional bounded display label; no contact details are returned. Results
are limited to 20 with an explicit `HasMore` indicator.

A result is a momentary selection candidate, not assignment authority. Any
future approval and assignment must recheck designation, target actor, membership,
grant, coverage, case version and approver authority under appropriate locks.
Availability, workload and category-specific policy are not currently represented,
and must not be inferred from this lookup. Controlled designation provisioning,
operator authorization, data stewardship and production database grants remain
to be completed and independently reviewed before this directory is enabled
for operational use. Threshold policies and officer assignment are not supplied
by this slice.

An internal designation service additionally requires `Ellai:Operational:OfficerDirectory:AdministrationEnabled`
and the directory gate, both off by default. It requires a current, separately
provisioned administrator event for the exact organization, country and sector,
an active administrator membership and access epoch, and prohibits self-designation.
Successful changes and command receipts are append-only and versioned. Authority
events are also append-only; a later revoked or suspended event supersedes an
earlier grant. The migration creates no administrators. The designation-change
endpoint uses the authenticated principal and requires antiforgery for cookie
sessions; it does not grant administrator authority. There is no administrator-grant
workflow yet. Production database
roles must be separated so the application cannot grant itself administrator
authority, and an approved bootstrap and revocation process must be established
before either gate is enabled. A designation is not a professional licence check.
