# SW-01-010 transport, storage and recovery audit - 2 November 2026

## Outcome

Local Compose source is development-oriented and binds published ports to loopback. It uses authenticated NATS, a Qdrant API key, persistent named volumes and Gateway Data Protection keys. It explicitly configures Gateway as Development and MinIO internal traffic as HTTP without SSL. No production TLS termination/authenticated internal-network deployment, encryption-at-rest configuration, backup schedule/retention, or restore-drill evidence was found in the reviewed repository paths. SW-01-010 remains partial/blocked; source configuration is not deployment proof.

## Evidence

| Area | Source evidence | Result and limit |
|---|---|---|
| Gateway HTTPS | `src/SwiftAid.Gateway/Program.cs` calls `UseHttpsRedirection`; `infra/compose.yml` publishes Gateway on `127.0.0.1` and sets `ASPNETCORE_ENVIRONMENT=Development`. | Local loopback binding is observed. No production reverse-proxy/TLS config or deployed certificate validation was found here. |
| Internal service authentication | Compose configures NATS Gateway/worker credentials and a Qdrant API key; Redis requires a password. | Configuration presence only; runtime ACL/credential validation is not part of this audit. MinIO uses a local HTTP endpoint and `Minio__UseSsl=false`. |
| Storage persistence/protection | Compose defines named PostgreSQL, Qdrant, MinIO, NATS and Data Protection key volumes. Gateway protected-payload options include a persistent key path and development-only unencrypted-key allowance. | Named volumes establish persistence configuration, not encryption-at-rest, access controls, backup or restore correctness. Development allowance must not be mistaken for production key protection. |
| Published ports | Gateway, PostgreSQL, NATS client, MinIO API and Redis ports in `infra/compose.yml` bind to `127.0.0.1`. | The Compose source reviewed does not publish these ports on all interfaces; deployed host firewall/proxy reachability is NOT VERIFIED. |
| Backups and restore | Repository infrastructure inventory contained no backup/restore runbook or verified SwiftAid restore evidence. | Backup configuration, retention, encryption, point-in-time recovery, object/key recovery and restore drill NOT VERIFIED. No restore was attempted. |

## Required closure

Provide production TLS termination and internal transport-auth configuration; verify secret/certificate provisioning and rotation; define and verify encryption/key management for PostgreSQL, object storage, Qdrant and backups; document coordinated database/object/Data Protection key backup and retention; and complete a disposable restore drill that proves records, original media, outbox state and protected payload access recover consistently. Do not treat loopback-only development ports as production exposure evidence.

No secret values were read, and no deployment, database, storage or restore operation was performed. No test was run for this source/configuration audit.


## Refreshed active Gateway role finding - 3 November 2026

Read-only inspection of the running Gateway container extracted its configured database username without exposing the connection string or password. PostgreSQL reported role `swiftaid_dev` with superuser, create-database, create-role, replication and row-security-bypass attributes; a privilege query counted INSERT/UPDATE/DELETE on all 34 public tables. This confirms the current local Development Gateway does not use a least-privilege application identity. No database role or grant was modified. Treat this as a release blocker until a separately provisioned non-superuser application role has reviewed minimum grants and the deployed configuration is verified. Production TLS, encryption-at-rest, backup and restore evidence also remain absent.