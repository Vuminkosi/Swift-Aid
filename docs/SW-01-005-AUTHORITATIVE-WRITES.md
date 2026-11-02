# SW-01-005 authoritative write-path audit - 2 November 2026

## Outcome

Source/configuration review is partial. The managed case API routes through Gateway endpoints and application command processing, and runtime grant scripts constrain a configured PostgreSQL role. The Compose configuration supplies the same `POSTGRES_USER`/`POSTGRES_PASSWORD` variables to Gateway and the migration runner; the active database role privileges were not inspected. Classification worker configuration explicitly supplies dedicated NATS credentials and no PostgreSQL or Qdrant endpoint/credential. Its optional environment file was not read. Runtime identity and the full worker fleet remain NOT VERIFIED.

## Evidence map

| Concern | Evidence | Result and limit |
|---|---|---|
| Managed case writes | `src/SwiftAid.Gateway/Endpoints/CaseCommandEndpoints.cs`; `src/SwiftAid.Application/Cases/Commands/CaseCommandProcessor.cs`; `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs` | Case command API maps accepted wire commands to application processing and persistence. This source trace does not enumerate every authoritative write endpoint/table. |
| Restricted database role | `infra/postgres/case-command-runtime-grants.sql`; `infra/postgres/case-import-runtime-grants.sql` | Scripts define preconditions and constrained table/column privileges for a supplied runtime role. Presence of scripts does not prove deployment applied them or that active Gateway credentials use that role. |
| Compose DB identities | `infra/compose.yml` Gateway and migration-runner service definitions | Both use `POSTGRES_USER` and `POSTGRES_PASSWORD`; migration runner also accepts an override connection string. Actual effective usernames/privileges were not revealed or queried. |
| Worker credentials | `infra/compose.yml` `classification-worker` service; `workers/src/swiftaid_workers/classification/` | Dedicated `NATS_WORKER_USER`/`NATS_WORKER_PASSWORD` are configured. No explicit database or Qdrant setting is in the service block. Worker uses Gateway loopback URL with loopback HTTP opt-in. Optional env-file contents are uninspected and cannot be ruled out. |
| Qdrant boundary | Gateway has Qdrant configuration; classification service block has none. | Source/configuration indicates the classification worker is not directly configured with Qdrant credentials; runtime process environment and network reachability were not independently verified. |

## Required closure evidence

- Verify the live Gateway connection identity and effective privileges against the least-privilege role contract without disclosing credentials.
- Verify migrations/bootstrap use a distinct migration identity and the restricted application runtime grants are applied.
- Verify every worker's effective environment and network policy contains no global database or Qdrant credential; retain only minimized, scoped job access.
- Trace all managed-record write routes to authorized Gateway application commands and check worker database access independently.

No database, deployment, credential, contract, or configuration was changed. No database query was issued in this source/configuration audit. Ownership and deployment handoff remain required under BL-005; no destructive or active scan was run.


## Refreshed runtime boundary evidence - 3 November 2026

Read-only `docker inspect` of the stopped `swiftaid-classification-worker-1` reported state `exited` (255), a `/models` mount, and network mode referencing Gateway container ID `4e0815a599e9d84dbc2d9fcfcaa6f91197774e4e1ea47adddd5c0328c09e30c3`; the current Gateway ID was `c1d828ebe046dae509728dcf3f2a44b16ad7b329e80aac9f7e90f872015858e2`, so the namespace does not match. The worker's stored environment variable *names* include NATS credentials, a Gateway worker token, and classification settings; no PostgreSQL connection or Qdrant endpoint/key variable name was present. Values were not printed. Since the worker is stopped and its namespace is stale, this does not prove its running network access or actual token scope.

The live Gateway container configuration was inspected in memory to extract only the database username (`swiftaid_dev`), without printing its connection string or password. A read-only query against PostgreSQL `pg_roles` reported `rolsuper`, `rolcreatedb`, `rolcreaterole`, `rolreplication`, and `rolbypassrls` all true. `has_table_privilege` counts showed INSERT, UPDATE and DELETE privileges on all 34 public tables. This is a confirmed local Development configuration finding; no role or grant was modified. SW-01-005 remains partial, and production authorization/least-privilege closure is blocked under BL-013.