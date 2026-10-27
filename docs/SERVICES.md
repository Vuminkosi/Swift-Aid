# Service catalog and configuration

Snapshot: 27 October 2026. Contract basis: spec v1.4 and implementation plan v2.4, plus the owner decisions recorded in [SPEC-ALIGNMENT.md](SPEC-ALIGNMENT.md). Source inspection covers canonical HEAD `369d44a47fab839533dddd3060552f23089f3309` and the current processor/persistence/schema worktree. This catalog separates approved roles, source wiring, and runtime proof. It is not a deployment recipe or service-health report.

## Boundaries

- PostgreSQL is the server-side authority for case records, grants, approvals, provenance, versions, and operational state. Redis and Qdrant are derived services and cannot make authorization or case decisions authoritative.
- MinIO is the selected private object store for original files, audio, and video. Mobile and browser clients must use gateway-controlled access and never receive object-store credentials.
- Redis is the owner-selected backend application cache and exact-result cache, with bounded TTL. It is not an authorization-decision cache or a source of truth.
- Qdrant is limited to scope-filtered AI memory/search indexing. Every retrieval is governed by current PostgreSQL authority; Qdrant is not a case database or an unrestricted client/worker endpoint.
- NATS JetStream carries configured background work and in-app notification events. A JetStream acknowledgement means broker acceptance only; it does not mean a worker completed an event or a person received/read a notification.
- FCM is optional push delivery. It is disabled in defaults and is not a prerequisite for ordinary case work.
- Native SQLite read models and drafts and bundled UI assets remain on the client. Server service secrets belong only to gateway/server configuration; they must never be placed in Mobile or browser configuration.

The existing plan §3.3 says no Redis is required. The separate owner decision recorded in the alignment ledger selects Redis for all backend cache and exact-result cache use. This is an implementation choice, not a change to other product requirements.

## Configuration contract

The Gateway binds provider settings from [appsettings.json](../src/SwiftAid.Gateway/appsettings.json) and infrastructure option types. ASP.NET Core configuration environment overrides use `__` for section separators (for example `Nats__Url`). The approved provider roots are exactly:

`Minio`, `Redis`, `Nats`, `Fcm`, `Qdrant`, `Meta`, `Federation`, and `ConnectionStrings:SwiftAid` are the provider/database roots. `DataProtection` is a separate server-side key-ring/security section; `TransportSecurity` is a separate transport guard section.

`appsettings.json` leaves server credentials and connection strings empty and provider `Enabled` flags false. Compose/environment/private configuration may override these defaults. Keep credentials in a protected server-side environment or secret store; do not commit real values, put them in static Mobile/browser assets, or copy them into logs. The checked-in `.env.example` contains replacement placeholders, not usable credentials.

| Root section | Stable option names in source | Default in Gateway appsettings | Enabled behavior and configuration gate |
|---|---|---|---|
| `Minio` | `Enabled`, `Endpoint`, `Bucket`, `MaxObjectSizeBytes`, `AccessKey`, `SecretKey`, `UseSsl` | Disabled; empty endpoint/credentials; bucket `swiftaid-private-media`; max size `0`; SSL false | When enabled, requires an absolute endpoint, valid private bucket, positive object limit, and credentials. `UseSsl` must agree with HTTP/HTTPS. Use an operator-provisioned private endpoint. |
| `Redis` | `Enabled`, `ConnectionString`, `InstanceName`, `MaximumTtlSeconds` | Disabled; empty connection; prefix `swiftaid:`; max TTL `3600` seconds | When enabled, requires a valid connection, prefix ending in `:`, and positive maximum TTL. Each cache write requires positive TTL no greater than this bound. |
| `Nats` | `Enabled`, `Url`, `Username`, `Password`, `EventStream`, `EventSubjectPrefix`, `NotificationStream`, `NotificationSubject` | Disabled; URL/credentials empty; streams `SWIFTAID_EVENTS`, `SWIFTAID_NOTIFICATIONS`; subjects `swiftaid.events.case`, `swiftaid.notifications.inapp` | When enabled, validates URL, credentials, stream identifiers, and subjects. Gateway publishing is JetStream-only; see NATS section below. |
| `Fcm` | `Enabled`, `ProjectId`, `CredentialsFilePath` | Disabled; strings empty | When enabled, requires project ID and an existing fully-qualified server-side credential file. No live send was verified. |
| `Qdrant` | `Enabled`, `Endpoint`, `ApiKey` | Disabled; endpoint/key empty | When enabled, validates endpoint and key. The current source registers options but has no Qdrant client/package or scoped retrieval implementation. |
| `Meta` | `Enabled`, `Provider`, `RouteAlias`, `ModelId`, `ApiKey` | Disabled; strings empty | When enabled, validation requires provider `meta`, explicit route/model and credential. This configuration is not a provider adapter, capability probe, or evidence of a live Meta call. |
| `Federation` | `Enabled`, `Authority`, `ClientId`, `ClientSecret` | Disabled; strings empty | When enabled, validates HTTPS authority and client credentials. No provider registration/callback or identity runtime is verified by this catalog. |
| `ConnectionStrings:SwiftAid` | Connection-string value | Empty | `SwiftAidDbContext`, persistence mappings and additive P2.1A migration are present; P2.1B-SQLAUTH lease/authorization code is integrated. This does not prove production DI/database readiness or a live connection. |

### DataProtection server-side protected-payload settings

`DataProtection` is a separate server-only security section, not one of the provider roots. Defaults are disabled, empty strings, `AllowUnencryptedDevelopmentKeys=false`, and `MaximumPayloadBytes=0`:

| Property | Purpose | Default | Enablement requirements |
|---|---|---|---|
| `Enabled` | Enables protected case-event payload handling | `false` | Must be explicitly enabled; otherwise the store is unavailable. |
| `ApplicationName` | Data Protection application discriminator | Empty | Non-empty canonical value, no surrounding whitespace, maximum 128 characters. |
| `KeysPath` | Persistent key-ring directory | Empty | Existing absolute directory. |
| `CertificatePath` | Certificate protecting the key ring | Empty | Outside Development, an existing absolute certificate file is required together with its password. |
| `CertificatePassword` | Certificate password | Empty | Required together with `CertificatePath`; keep in protected server-side configuration. |
| `AllowUnencryptedDevelopmentKeys` | Explicit Development-only plaintext key-ring opt-in | `false` | Permitted only in Development. Without a certificate, it must be explicitly enabled in Development. |
| `MaximumPayloadBytes` | Maximum plaintext payload accepted for protection | `0` | Must be positive and supported by the runtime. |

Never place key-ring or certificate secrets in Mobile/browser configuration or in MinIO media storage. Key-ring persistence permissions, deployment volumes and certificate provisioning remain operational gates.

Transport guard properties are `TransportSecurity:AllowPlaintextDevelopment` (default `false`) and `TransportSecurity:AllowedDevelopmentHosts` (default exact hosts `localhost`, `127.0.0.1`, and `::1`). TLS is required for enabled MinIO, Qdrant, NATS, and Redis unless all of these conditions hold: the actual host environment is `Development`, plaintext is explicitly enabled, and every endpoint host exactly matches an allowlisted host. Host comparison is case-insensitive and bracket-normalizes IPv6; it has no wildcard or port matching. Production and missing-host-environment registration fail closed. Compose opts into Development plaintext and allowlists only its private service names `minio`, `redis`, `nats`, and `qdrant`.

The guard accepts only exact allowlisted hosts in Development when plaintext is explicitly enabled; it has no wildcard or port matching, and Production requires TLS. The default host list is loopback. An explicitly configured allowlist can include a non-loopback host; the accepted S3 disposition is that this exception is intentional for private Docker Development hosts only. Do not generalize that approval to arbitrary remote development endpoints or production. The source security review and independent configuration suite passed; no live provider TLS handshake was tested.

Options and validators are in [Configuration](../src/SwiftAid.Infrastructure/Configuration/ServiceCollectionExtensions.cs) and [ProviderOptionsValidator.cs](../src/SwiftAid.Infrastructure/Configuration/ProviderOptionsValidator.cs). Validation checks configuration shape only; it does not contact or prove health of a service.

## Service records

### PostgreSQL and native SQLite

**Approved role:** PostgreSQL/PostGIS is the server authority. Native SQLite supports offline local drafts/read models and synchronization boundaries; Datasync is the approved client synchronization model, not database-to-database replication.

**Current source status:** `ConnectionStrings:SwiftAid` is configurable but empty by default. `SwiftAidDbContext`, persistence mappings, and the additive P2.1A migration are present; the migration/schema slice is committed at `a467b4f9cf220987cb081c3befe7f34b72e851a8`. The case-bound P2.1B-SQLAUTH authorization/lease slice is committed at `cc3d72d26ffdaf40ce297fed010f52f5d3f440a6` (16 paths) and its post-commit `CaseAuthorityPostgresTests` fixture run passed 13/13 at that HEAD. This is bounded disposable-fixture evidence, not production database readiness: runtime DB-role lock privileges/helper/bootstrap, concurrent inserts or updates of absent authority rows and epoch increments, final DB-clock command/read guards, and production DI/processor remain NOT VERIFIED. SQLite/Datasync package presence does not prove an offline read model, process-death recovery, or Android sync flow. Database deployment, backup/restore, and native offline acceptance remain NOT VERIFIED.

### Protected case-event payloads

**Approved role:** encrypt supported case-event payload bytes at rest through the server-side Data Protection key ring. Uploaded originals, audio, and video remain in MinIO; this path is not media storage.

**Current source status:** `Microsoft.AspNetCore.DataProtection.Extensions` 10.0.14 is referenced by Infrastructure. The `DataProtection` options default disabled. When enabled, a validated persistent key-ring directory and positive payload-size limit are required; outside Development the key ring must be certificate-protected, while unencrypted keys require explicit opt-in in Development. The payload store stages ciphertext in the active case transaction and does not save the DbContext itself. Protection purpose is bound to case ID, payload ID, event type, schema version, and purpose version. Before unprotecting, the store checks the persisted payload bytes and purpose plus the linked CaseEvent tuple; only then does it decrypt.

Independent source/API/scoped R3 reviews PASS. The core protection source/package/config is committed at `369d44a47fab839533dddd3060552f23089f3309`. Integration.Tests build and opt-in disposable PostgreSQL run are historical evidence at pre-commit HEAD `cc3d72d26ffdaf40ce297fed010f52f5d3f440a6`: the build passed with .NET SDK 10.0.403 and no warnings/errors; the `ProtectedPayloadPostgresTests` run passed 5/5 (three database fixture tests, two configuration tests), no skips/failures, with synthetic row counts zero before and after. No post-commit rerun is reported. Gateway/production DI invocation, key-ring ACL/deployment volume, production DB-role restrictions, processor integration, and end-to-end protection acceptance remain NOT VERIFIED. Separate uncommitted A/B/C processor, Infrastructure persistence/journal, and schema/helper slices are detailed with hashes in `PROGRESS.md`; no build/test/migration execution is claimed for them. `CaseEventJournalReader.cs` is in slice B: source review PASS, executable verification and canonical integration pending. Uploaded media remains MinIO.

### MinIO private object storage

**Approved role:** private storage for original files, audio, and video; PostgreSQL retains references and authority. Do not replace originals with transcripts or derived content.

**Current source status:** the MinIO 7.0.0 package is resolved in the Infrastructure lock file. `MinioObjectStorage` implements bounded store/read operations behind `IObjectStorage`, and Gateway configuration selects it only when `Minio:Enabled=true`; otherwise the service returns `NotConfigured`. The validator requires credentials and a private endpoint when enabled, with TLS unless the explicit Development host exception applies. An operator-provisioned private endpoint remains the supported production integration.

For local development, the Compose file now builds local-only amd64 MinIO and `mc` images from checksum-pinned official source archives using [the MinIO Dockerfile](../infra/containers/minio/Dockerfile) and [the bootstrap Dockerfile](../infra/containers/minio-bootstrap/Dockerfile). The image references in Compose identify these local build outputs; they are not published registry artifacts. The official source tags are MinIO `RELEASE.2025-10-15T17-29-55Z` (commit `9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a`, archive SHA-256 `BE6D0BD3696C3A13A35F02D3A0280B64319C67918B4501C5C3D87F96D000085C`) and `mc` `RELEASE.2025-08-13T08-35-41Z` (commit `d6541ea280b73a834b64d4097e21f2be77676104`, archive SHA-256 `29DB22500374169A43951C7CEF09DAF19E7291EA5BA00AC10F321371B0A35B32`). Compose pins amd64 build inputs; owner-reported local image digests are MinIO `sha256:397f6fe48a541ac1f5933afea3fcd65dc963bc1feaf4e9ca30a4e51e523b2ca3` and `mc` `sha256:b0bb3c6dd66f28dfcc8380ed28c0f66df47c7c5fc7a66d49c1c683ba44ce14b9`.

The source archives retain upstream LICENSE and CREDITS in the resulting images. The MinIO and `mc` upstream repositories report archived/read-only status ([MinIO](https://github.com/minio/minio), [`mc`](https://github.com/minio/mc)); these builds are local-only and do not establish ongoing upstream maintenance or a supported public image. DevOps reported both source builds and version checks PASS, followed by an isolated private bootstrap, 52-byte SDK store/read round trip, anonymous HTTP 403, and refusal to modify an existing bucket. A separate opt-in disposable application-DI integration fixture also passed: it checked MinIO HTTP `/minio/health/ready`, bucket initialization and the configured adapter path. This is bounded fixture evidence, not a full Compose launch, production readiness, or operational acceptance. Full Compose stack, production storage workflow, encryption/retention/lifecycle, backup, and recovery acceptance remain NOT VERIFIED.

### Redis cache

**Approved role:** all backend application cache and exact-result cache use with TTL. The default prefix is `swiftaid:` and maximum TTL is 3600 seconds; both are configurable. Every write must use a positive TTL at or below the configured maximum.

**Safety boundary:** cache data is derived and revocable. Do not cache permission or authorization decisions. A cache hit must not bypass current access checks, input freshness, capability gates, or output validation. The current `RedisCacheStore` enforces the TTL bound and reports cache failures; when disabled, `NotConfiguredCacheStore` is registered.

**Current source status:** `StackExchange.Redis` 3.4.20 and `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.14 are in the current central package and lock records. `MaximumTtlSeconds` has a source-supported range of 1–86400, with default 3600. DevOps reported an isolated authenticated Redis SET/GET check and four-second TTL expiry after five seconds. A separate opt-in disposable application-DI integration fixture passed for cache miss/hit/write, TTL bound rejection, removal, and expiry. This does not establish a full Compose launch, production readiness, or product cache workflows. Exact-result product policy and authorization remain governed by the spec and application layer; no permission or authorization-decision caching is approved.

### Qdrant scoped AI memory/search index

**Approved role:** private, scope-filtered memory/search index only. PostgreSQL remains authority for source records, grants, consent, provenance, versions, expiry, invalidation, and approvals. Raw media, credentials, and arbitrary unscoped query access are out of bounds.

**Current source status:** `Qdrant` options and validation exist, and Compose describes a private API-key protected service. No Qdrant client package, adapter, retrieval/write code, or Qdrant service runtime was found in the current tree. Embeddings and semantic retrieval are not enabled by this source; exact/ID retrieval is not implemented or verified either. Scoped access, stale-item denial, invalidation, cleanup, and Qdrant outage behavior remain NOT VERIFIED. Do not call this service available merely because endpoint options exist.

### NATS JetStream and in-app notifications

**Approved role:** JetStream streams/queues for backend work and event publication, plus scoped in-app notification events. Optional push uses FCM separately.

**Current source configuration:** Gateway defaults are `EventStream=SWIFTAID_EVENTS`, `EventSubjectPrefix=swiftaid.events.case`, `NotificationStream=SWIFTAID_NOTIFICATIONS`, and `NotificationSubject=swiftaid.notifications.inapp`. These names are corroborated by the Gateway settings, Compose overrides, and [NATS ACL configuration](../infra/nats.conf). The ACL file allows the Gateway principal to publish the configured job/event/notification subject families and request stream info for `SWIFTAID_JOBS`, `SWIFTAID_EVENTS`, and `SWIFTAID_NOTIFICATIONS`; a separate worker principal has job-consumption and result/progress permissions. No operator/bootstrap principal is defined in that file.

Gateway publishers call JetStream publish with an expected stream and message ID. They do not create or update streams and do not fall back to core NATS. Stream definitions are not present in `infra/nats.conf`; an operator must provision the required streams before enabling this integration. A missing or unavailable stream is reported as unavailable/failure by the publisher. A positive JetStream acknowledgement confirms broker acceptance only, not worker completion, user delivery, read, or assistance receipt.

**Verification boundary:** a reported isolated NATS ACL run passed 2/2 on a disposable NATS Server 2.15.1 container; worker publish and Gateway subscribe to the tested job subject were denied, while Gateway publish and worker receive were allowed. A separate reported NATS.Net 3.3.1 SDK check stored a file-backed JetStream message with sequence 1; publishing to a missing expected stream raised `NatsJSApiException` without changing the stored-message count. An opt-in disposable application-DI integration fixture also passed for event/notification publishing acknowledgement and missing-stream failure. The current Compose manifest does not define a worker service; a worker principal in `nats.conf` is ACL configuration only. These are bounded broker/ACL/adapter checks, not a full Compose startup, stream provisioning, consumer processing, production readiness, or in-app notification delivery. A broker acknowledgement confirms broker acceptance only, not user delivery/read. The reported Compose config parse is syntax evidence only.

### FCM optional push

FCM is disabled by default and requires project ID plus a server-side credential file when enabled. `FcmPushNotificationSender` is configured only with `Fcm:Enabled=true`; otherwise the no-provider implementation is used. The current provider configuration suite reported 19/19 passing, including serialization of the `DeviceToken` field as the Firebase `token` property and invalid-configuration rejection without exposing secret values. The suite used synthetic input and did not create a Firebase client, access credentials, call a live endpoint, or send a notification. FCM delivery, consent, token lifecycle, privacy payload and receipt behavior are NOT VERIFIED. Push is optional and not a casework availability dependency.

### Meta and federation configuration

`Meta` and `Federation` are recognized server-only configuration roots, disabled and empty by default. Their validators reject malformed enabled settings, but no live Meta inference/ASR route or federation/identity provider runtime is established here. Keep inference Meta-only under the product contract; configuration values alone do not prove endpoint access, model capability, credits, pricing or policy approval.

## Runtime and operator gates

No real service credentials are recorded. The independent security review covered configuration shape, private network bindings and NATS ACL scope. The current profile parse command `docker compose --profile local-bootstrap -f infra/compose.yml config --format json` and the DevOps assertions reported PASS, but they do not start services. A separate opt-in disposable application-DI integration run is reported below; it does not mean the full Compose stack is running or providers are production-ready. Its exact command was `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --verbosity minimal` with process-scoped `SWIFTAID_RUN_LOCAL_PROVIDER_FIXTURES=1` (.NET SDK 10.0.403): 22 passed, 0 failed, 0 skipped, including all three opt-in provider adapter tests. Independent fixture code review passed for `ProviderAdapterContainerTests.cs` SHA-256 `05538E8624BC97DC43F1D5278661D5B0F977CEDD610906E3AE9F17D4AE5A12E6`; post-run fixture-label inspection found no remaining containers. Full Compose startup, service readiness, production credentials/TLS handshakes, broader application workflows, and live provider acceptance remain NOT VERIFIED. The reported `dotnet package list --project SwiftAid.sln --vulnerable --include-transitive --no-restore --format json` query returned no vulnerability entries across the 13 solution projects it listed; Mobile was absent from that query. This is scoped advisory evidence, not a complete dependency, license, or SBOM audit. Do not infer provider readiness from a Compose override, package lock, option validator, isolated SDK check, bounded test fixture, provider interface, or liveness endpoint.

Relevant source: [Gateway appsettings](../src/SwiftAid.Gateway/appsettings.json), [infrastructure registration](../src/SwiftAid.Infrastructure/Configuration/ServiceCollectionExtensions.cs), [Compose](../infra/compose.yml), and [NATS ACLs](../infra/nats.conf). Package versions and audit scope are recorded in [DEPENDENCIES.md](DEPENDENCIES.md) and [RIGHTS-REGISTER.md](RIGHTS-REGISTER.md).
