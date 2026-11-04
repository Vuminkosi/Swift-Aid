# Case CSV compatibility and Ellai capture assistance

The supplied `designs/data/zimbabwe_10500_cases.csv` and `designs/data/eswatini_4500_cases.csv` define the accepted version-one format. They are local, Git-ignored authoring fixtures and are not copied into source control. Both were read through the streaming adapter: 10,500 Zimbabwe records and 4,500 Eswatini records, including Human, Herd and Flock subjects. This check parsed and mapped records only; it did not ingest them into the shared application database.

```csv
schema_version,command_id,case_id,external_record_id,subject_scope,narrative,observations_json,captured_at,occurred_at,country_code,region,area,locality,location_id,latitude,longitude,location_precision,accountable_owner_id,workspace_id,source_language,source_reference
```

`observations_json` remains an array of original strings, escaped as a CSV field. Objects, null/empty observations, more than 32 observations or individual observations over 300 characters are rejected. Narrative is bounded to 6,000 characters. Empty narrative is allowed when observations supply evidence. Missing clinical fields remain explicitly unknown; neither the parser nor Ellai infers diagnoses, species or animal counts.

- `command_id` is a stable nonempty UUID retained on retry. `case_id` is the **source** case identifier, not a request to overwrite or choose a destination managed-case ID. The registration service assigns destination identity and returns a durable receipt.
- `external_record_id`, `source_reference` and `source_language` remain original source metadata. Original field values and their SHA-256 hash are available to the import ledger independently of normalized report facts.
- Empty `workspace_id` uses the authorized import context. A supplied workspace must match it. Empty `accountable_owner_id` uses the submitting officer via normal registration; a different supplied owner is rejected. Import does not bypass assignment workflow.
- The country must match the explicitly reviewed import context. Region/area/locality names remain reported names; supplied `location_id` is a reported normalized reference, not proof of geographic verification.
- Times retain the supplied offset and normalize actual instants to UTC; no server-local timezone inference. Empty times remain unknown. Date-only occurrence precision is supported.
- Both supplied files omit coordinates. Future coordinate rows must include an optional `accuracy_metres` column; the adapter never invents coordinate accuracy. Existing geographic bounds/precision rules still apply.
- Header order may vary, but required column names must be present once. Unknown columns are rejected, preventing silently dropped source fields.

`BoundedCsvReader` reads 4,096 characters at a time, supports quoted commas/newlines/doubled quotes and bounds records to 65,536 characters, fields to 16,384 characters and columns to 32. Malformed CSV or an overlarge field stops at the failing structural boundary; earlier durable records must be reconciled by the import service. No full-file `ReadToEnd` or collection is used in this path. The development demo seeder remains separate.

## Ellai tools

- `ellai_ingestion_schema`: returns the current format, capture requirements and authority boundaries.
- `ellai_validate_observations`: validates string-array shape and bounds; returns only count/format status, without echoing the evidence or claiming submission.
- `ellai_locations`: uses the existing public registry, returns bounded candidates with registry-version provenance, and requires user selection.

These tools run through the existing broker, session scope, budget, catalogue and progress narration. They are available in the public capture-assistance profile without exposing case queues, receipts or staff authority. They do not submit cases or approve administrative actions. Existing report forms and canonical services own durable submission.

## Durable workspace imports

Organization creation now atomically provisions creator grants for the initial workspace: Zimbabwe and Eswatini, human and animal health, standard sensitivity, and `ReadCase`, `Search`, `Count`. `ReadCase` is the existing registration prerequisite. These scoped grants allow jurisdictional visibility only inside the same organization/workspace; invitations and ordinary membership do not provision them. Grants have no scheduled expiry but remain subject to live actor, membership, resource and grant status checks.

Automatic migration `20261101110000_OrganizationCreatorAccess` backfills only active original creators whose self-granted membership timestamp matches organization creation and an allowed `Organization.Created` audit. It skips any membership/workspace with existing grants, including revoked or expired grants. Backfill operations and audit entries commit together and reruns add no duplicates. Automatic downgrade is refused because accepted cases may depend on these grants; revoke through controlled administration and fix forward.

Creator-access verification (2026-11-01): 19 focused membership/invitation/workspace tests passed, including real PostgreSQL creator import, revocation and repeated backfill checks; 11 import and automatic-migration deployment tests passed. The creator regression failed with zero grants before the correction. These synthetic integration results do not establish completion of the supplied 10,500-row browser import.

The import form provides separate **Organization and workspace** and **Occurrence country** selectors. Workspace choices include active destinations linked to the signed-in user's active organization memberships, even when case grants have not yet been configured. Selection does not grant case access: the form explains missing import access and disables submission for unauthorized workspace/country combinations. Registration still validates country, sector and sensitivity grants for each row. Expired memberships, revoked membership and stale authentication epochs cannot expose destination choices.

Imports belong to an authorized workspace. Open **Workspace → Imports** (`/workspace/imports`), or **Cases → Import cases**. Select an authorized workspace/country, choose the supplied-format CSV, review its details and start. The page streams files up to 64 MiB in batches bounded to 100 rows and 1 MiB of UTF-8 text. Pause finishes the current bounded batch; leaving the page cancels its capture/processing lifetime without undoing committed cases. Recent imports and paged sanitized rejection details restore from PostgreSQL after refresh. Resume requires selecting the unchanged original file. No credentials or command-line setup are required in the UI.

The bounded HTTP contract also supports operational automation. The web workspace uses the same application service through a short-lived service scope per operation; authenticated server identity supplies the actor, and every batch/row rechecks live access. The destination catalogue is bounded to 100 currently authorized workspace/country pairs; recent history to 20 owner-only imports for the selected scope. Source row text is not returned by history queries. Native Android/iOS import navigation is not implemented in this slice; responsive browser layout is not native-app verification.

`POST /api/v1/case-imports/{importId}/rows?startRow=1&finalBatch=false` accepts UTF-8 `text/csv`, at most 1,000 rows and 8 MiB per HTTP batch. Every batch repeats the header. `X-SwiftAid-Import` carries base64 UTF-8 JSON:

```json
{
  "workspaceId": "your-authorized-workspace",
  "confirmations": {
    "occurrenceCountryConfirmed": true,
    "occurrenceCountryCode": "ZW",
    "occurrenceCountryCatalogueVersion": "current-catalogue-version",
    "reportReviewConfirmed": true,
    "processingConfirmed": true,
    "processingPolicyReference": "current-policy-reference",
    "processingPolicyVersion": "current-policy-version",
    "recordingConfirmed": null,
    "recordingPolicyReference": null,
    "recordingPolicyVersion": null
  }
}
```

Use current metadata/policy and actual user confirmations. Cookie requests require the existing antiforgery token; native bearer authentication follows the existing registration scheme. Guest intake receipts never authorize this endpoint. `GET /api/v1/case-imports/{importId}` returns owner-authorized durable counts and the next row, with live membership/grant/epoch checks.

The ledger protects original row values using the configured data-protection key ring before invoking canonical managed-case registration. Source hashes, context, stable command IDs and lease fencing prevent changed-source replay and concurrent checkpoint corruption. Each case uses the existing registration transaction, protected events, receipt and domain outbox. A crash after case commit but before checkpoint retries the original command and receives its receipt, without creating a second case. Preserve key-ring material across restarts.

States are Running, Paused, Completed and Failed. Row results distinguish Accepted, Replayed, Invalid, Denied, Conflict, RetryableFailure and Failed. Completed means the supplied file was processed; inspect rejected counts. Registration unavailability retries at the same checkpoint, bounded to five attempts. Interrupted/unavailable streams pause; earlier accepted rows remain committed. Resume with the same import ID, context, header order and unchanged source file. Completed imports cannot append rows. No automatic background reprocessing or source-file retention in MinIO is claimed; the caller resends remaining rows.

Policy/country validation dependency exceptions now also produce durable RetryableFailure rows and exhaust after five attempts, with a sanitized stage-specific error. They do not leave a row indefinitely Processing. Explicitly reprocessing the original row after dependency recovery preserves the same command and can resolve the failure without duplication. Persistence/checkpoint outages remain recoverable through lease expiry and canonical receipts; a database outage cannot be falsely reported as a persisted terminal result.

The 8 MiB HTTP batch limit is enforced while reading actual wire bytes, including requests without Content-Length. A body crossing the limit returns HTTP 413 with the durable partial result and paused checkpoint. Earlier committed cases remain saved. Reduce the next batch and resume at the returned checkpoint; do not assume a rejected oversized request rolled back its earlier rows. Workspace uploads already send smaller bounded batches.

For operational automation, `scripts/import-cases.py` streams bounded batches, queries the checkpoint before resuming, refuses redirects and reads its bearer token only from `SWIFTAID_IMPORT_ACCESS_TOKEN`:

```powershell
python scripts/import-cases.py designs/data/zimbabwe_10500_cases.csv --gateway https://your-gateway --context import-context.json --import-id YOUR-STABLE-UUID
```

The context file contains the reviewed object above, not credentials. HTTP is allowed only for loopback development. The command is optional tooling, not a replacement for the workspace import flow.

Deploy migration `20261101054203_DurableCaseImports` after the ingestion-outbox migration, followed by `20261101061649_SharedOrganizationWorkspaces`. Migration `20261101130000_InstallCaseAuthorityReadDependency` installs a missing authority helper with SECURITY INVOKER and no PUBLIC execute permission for owner-backed deployments. Existing helpers are preserved. Restricted deployments still provision `infra/postgres/case-authority-lock-helper.sql` with the reviewed dedicated helper owner and runtime grants. Restricted runtime grants for the new ledger require deployment verification before enabling imports outside development.

Verification on 2026-11-01: six real PostgreSQL / ASP.NET TestServer tests cover creation, encrypted source retention, reopened-context replay, injected checkpoint failure after committed registration, independent row rejection, lease conflict, immutable source, checkpoint gaps, owner/epoch denial, HTTP authentication, cookie antiforgery rejection and history retrieval. Authentication and policy are deterministic fixtures; live identity/provider/browser validation is NOT VERIFIED. The supplied 15,000-row test verifies parsing/mapping only, not database import throughput.

Verification: focused application tests cover malformed CSV, field bounds, cancellation, string-array compatibility, source preservation, unknown facts, owner mismatch, deterministic source hashes, tool scope/budget and registry provenance. The supplied-file fixture test is opt-in via `SWIFTAID_TEST_CASE_CSV_DIRECTORY`; missing local files are never silently fabricated.

## Shared workspaces

Ellai staff tools reuse these same application readers: `ellai_import_destinations` lists current authorized destinations; `ellai_imports` lists owner-only recent jobs for an explicit destination; `ellai_import_status` returns durable saved/rejected/retry counts and a checkpoint. They are excluded from the guest/public-help allowlist, take actor identity only from trusted server context, retain the existing call/result bounds and progress acknowledgement path, and never submit data or return original narrative/observations. Import completion is explicitly separate from surveillance analysis. Real broker/database tests cover owner isolation, stale access epochs, argument injection and public profile denial. Live Muse selection and spoken playback of these new tools remain NOT VERIFIED.

Owner-directed decision, 2026-11-01: multiple organizations can share a workspace. `workspace_organizations` stores explicit active/suspended/revoked links. Each case still belongs to exactly one organization; linking a workspace does not grant membership, country/sector permissions or access to another organization's cases. Access grants and case projections reference the composite link. Original workspace organizations are backfilled, and existing EF workspace-creation callers create the original link automatically.

The workspace Imports page selects an organization, workspace and occurrence country from current authorized destinations. New import contexts include optional `organizationId`; the managed registration API accepts the same optional field. It is checked against current membership and grants, persisted with the import and bound to the command hash. Omitting it preserves the original workspace organization and existing canonical receipt bytes. Null is omitted from import-context serialization so pre-existing imports can resume with their original hashes. Old clients remain compatible but cannot choose a secondary organization until they supply the new field.

Link revocation serializes against the existing workspace authority lock, including new registrations. Notifications, case access, command/assignment validation, membership workspace listings and import history honor the active link. Resuming an import reuses its durable organization context. History remains actor-owned and organization-filtered.

Link provisioning is an operator workflow using `infra/postgres/workspace-organization-link.sql`; no new self-service administration permission is inferred. See the operational runbook. Cases and source history are retained when a link is suspended or revoked. Automatic downgrade is refused when secondary links exist; fix forward.
