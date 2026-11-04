# Ellai health-officer eligibility boundary

The officer lookup is disabled by default (`Ellai:Operational:OfficerDirectory:Enabled`).
It has an authenticated case-scoped candidate endpoint and a separately gated
designation-change endpoint, but no model tool or public assignment action. A designation is a controlled organizational role record,
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
The internal assignment eligibility recheck now requires the target in the active
case write lease and locks the selected designation scope until that transaction
ends. It checks the exact designation ID and version against the same eligibility
rules as the list. This recheck alone does not authorize an approver, enforce the
feature gate, create an assignment or schedule follow-up.
The internal escalation approval service has its own default-off
`Ellai:Operational:OfficerDirectory:EscalationApprovalsEnabled` gate. It records
a 15-minute approval for one raised escalation, case version, officer designation
version, follow-up ID and acknowledgment/follow-up deadlines, after checking the
approver's case permissions and the officer's eligibility under a write lease.
It does not create the assignment or follow-up. A replay confirms only that the
approval was recorded; execution must separately check expiry, current authority,
case version and officer eligibility. The service has no HTTP or model endpoint.
The separately default-off `Ellai:Operational:OfficerDirectory:EscalationAssignmentsEnabled`
gate enables internal execution. It requires the original approver's current
authority and access epoch, an unexpired exact approval, the original case
version and raised escalation, and an eligible officer under the case write lease.
It atomically appends a normal case follow-up event and receipt, a follow-up
projection, outbox entry, and an immutable link to the approved escalation and
acknowledgment deadline. The existing case owner is unchanged. A deferred check
revalidates approval and designation expiry at commit. Execution does not notify
anyone. The authorized case timeline shows the escalation-to-follow-up link to
readers with both escalation and follow-up permissions, and distinguishes current
work from historical assignments. Ellai's case-history tool uses the same
permission-filtered projection and does not emit opaque officer identifiers.
There is no HTTP or model execution endpoint, no escalation acknowledgment
linkage, reassignment flow, or scheduled supervisor escalation yet.
The linked follow-up can be acknowledged through the existing work-management
flow. Its overdue acknowledgment can produce a permission-filtered review
recommendation, but that recommendation does not notify an officer or supervisor.
After a follow-up reschedule, the current projection cannot establish whether the
original assignment was acknowledged; the timeline reports that status as unknown.
An internal review query supplies a bounded list of raised, unassigned
escalations, the current case version and eligible officers under one authorized
case read lease. This is a point-in-time review context; approval and execution
still revalidate all authority and versions under a write lease. When all three
officer directory, approval and assignment gates are enabled, the authenticated
web case view provides explicit review, approval and a separate assignment action.
Uncertain outcomes retain their original command IDs for retry while the view is
open. There is no mobile review client in this slice.
Availability, workload and category-specific policy are not currently represented,
and must not be inferred from this lookup. Controlled designation provisioning,
operator authorization, data stewardship and production database grants remain
to be completed and independently reviewed before this directory is enabled
for operational use. Threshold policies are not supplied by this slice.

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
When both gates are enabled, the signed-in web organizations page offers a
lookup and explicit review before a designation change. An uncertain result
keeps the same command identity for retry. This is not a mobile administration
workflow and it does not provision the separate administrator grant.

The separately gated administration lookup accepts one explicit organization,
country, sector and actor reference in the request body. It uses the same current
administrator authorization and lock order as changes, and returns the latest
designation version even when suspended, revoked or expired. An authorized lookup
with no designation returns an empty current record; it does not establish that
the target exists or is eligible for assignment. Results are not cached. A later
change still checks the expected version and current administrator authority.
