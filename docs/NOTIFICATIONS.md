# Notifications

This guide describes the approved notification design and the source currently present in the
SwiftAid checkout. It is not evidence of a deployed service. The current `master` commit is
`e12ed1d`; notification backend (P6.3) and UI/push surfaces were committed there, and the
Compose/stream-script changes below are committed alongside this guide.

For the wider provider configuration and runtime evidence, see [the service catalog](SERVICES.md).
The product requirements are in [spec.md, section 21](../spec.md#21-notifications-operational-analytics-and-exports)
and the implementation plan in [implementation.md, section 10.1](../implementation.md#101-jetstream-topology).

## Status

| Status | Evidence |
|---|---|
| **APPROVED CONTRACT** | Notifications are recipient-scoped attention items linked to accepted work. PostgreSQL is authoritative. JetStream carries backend events; Redis is for caching only. FCM is optional and requires consent. External text is generic. Reading or dismissing an item never acknowledges or completes casework. |
| **IMPLEMENTED AND COMMITTED** | Gateway API/controller and web/mobile UI source, PostgreSQL item/device/outbox mappings, a `CaseAssigned` producer, a JetStream notification publisher, FCM sender, and web/mobile opt-in adapters are committed (backend P6.3 at `ef6f84c`, UI/push surfaces at `e12ed1d`). The current source uses Redis Pub/Sub for the live SSE change hint when Redis is enabled, which conflicts with the approved event-streaming decision. |
| **NOT VERIFIED** | Notification schema migration and PostgreSQL up/down behavior, current end-to-end tests, authenticated browser/mobile journeys, actual NATS stream provisioning and notification consumption, FCM provider delivery, device token refresh/revocation in a running app, and deployment are not verified by this documentation task. No notification migration file is present in the inspected migration directory. |
| **COMMITTED / RELEASED** | Backend and UI/push sources are committed as cited above; nothing is released or deployed. |

## How notifications are intended to work

1. An accepted `CaseAssigned` event creates one internal attention item for the accountable owner.
   The current producer derives its occurrence key from the accepted event ID and recipient ID,
   then adds the item and its delivery-outbox row to the case command persistence context.
2. The authenticated recipient loads the current feed from the Gateway. PostgreSQL owns the item,
   read/dismiss state and outbox. The stored item does not grant access to its case; the server
   checks current actor and resource authorization before showing it or dispatching a reminder.
3. A change signal tells an open client to refresh the authorized feed. The signal carries no
   notification, actor or case details. The browser/mobile reads the feed over the authenticated
   Gateway path; clients do not connect to NATS.
4. A background worker claims leased outbox rows, rechecks authority, publishes the internal
   event, and may send a generic FCM data message to registered devices. Broker/provider acceptance
   does not prove that a device received, displayed or was read by a person.

The current Gateway publishes to JetStream with the configured expected stream and notification
ID as the broker message ID. The code does not create or update a stream. The stream must be
provisioned by an authorized operator before the publisher can succeed. Stream provisioning uses
`infra/scripts/bootstrap-notification-stream.sh`, a read-only verify-or-create preflight that pins
the stream to `SWIFTAID_NOTIFICATIONS`, the subject to `swiftaid.notifications.inapp` and replicas
to 1, and validates the configured retention, size, duplicate-window and consumer ranges (see the
script for exact bounds). A successful provisioning run in a target environment is still
**NOT VERIFIED** here. Do not infer a successful stream setup from a Compose configuration parse
or NATS ACL entry.

**Current source gap:** when `Redis:Enabled` is true, Gateway DI selects
`RedisNotificationChangeSignal`, which uses Redis Pub/Sub for SSE change hints. The approved owner
decision reserves Redis for caches and requires NATS for event streaming. This live-feed transport
must move to the approved NATS path before this feature can be accepted. Polling exists as the
disabled-Redis path. The current source gap is not a change to the approved contract.

## Recipient API and client behavior

The current Gateway controllers expose these routes:

| Method and route | Use | Access and result |
|---|---|---|
| `GET /api/v1/notifications?pageSize=50&continuationToken=...` | Read the signed-in actor's feed | Cookie or validated bearer authentication; page size is 1–100. The feed is recipient-scoped. |
| `GET /api/v1/notifications/stream` | Wait for opaque feed-change and heartbeat events | Authenticated server-sent events. The current source emits empty `{}` data and a 20-second heartbeat. |
| `POST /api/v1/notifications/{id}/read` | Mark an item read | Authenticated; cookie requests validate antiforgery. |
| `POST /api/v1/notifications/{id}/dismiss` | Dismiss an item | Authenticated; cookie requests validate antiforgery. |
| `POST /api/v1/push/devices` | Register a device's push token and sound preference | Authenticated; cookie requests validate antiforgery. The token is accepted only for registration and is not returned. |
| `DELETE /api/v1/push/devices/{installationId}` | Revoke one device registration | Authenticated; cookie requests validate antiforgery. |

`read` and `dismiss` only change notification state; neither is a case action, escalation
acknowledgement or receipt of assistance. Generic feed text is derived from a closed notification
kind; the external FCM message contains only a kind and opaque notification ID. It does not include
a person, disease, exact location, case title or media excerpt.

Clients request push permission only after an explicit user action and confirmed staff feed access.
The sound preference is off by default and stored per device. The current UI offers a gentle sound
while SwiftAid is open; platform/background presentation and real device behavior still require
verification. The web service worker displays generic copy and opens SwiftAid.

## Configuration and service setup

Configuration defaults are in [Gateway appsettings](../src/SwiftAid.Gateway/appsettings.json).
Compose maps environment variables to ASP.NET Core configuration with `__` as the section
separator. The settings below are server-side; never put NATS or FCM server credentials in a
browser or mobile package.

| Setting | Current default / use |
|---|---|
| `Nats:Enabled` | `false` in Gateway appsettings; must be explicitly enabled. |
| `Nats:Url`, `Nats:Username`, `Nats:Password` | Empty defaults. Supply the server endpoint and least-privilege Gateway credentials through protected server configuration. |
| `Nats:NotificationStream` | `SWIFTAID_NOTIFICATIONS`. Must match the provisioned JetStream stream. |
| `Nats:NotificationSubject` | `swiftaid.notifications.inapp`. The stream must capture this subject. |
| `Redis:Enabled` | `false` in appsettings. Redis remains the approved cache service; do not use Redis Pub/Sub for notification events. |
| `Fcm:Enabled` | `false`. Push is optional. |
| `Fcm:ProjectId` | Empty. Firebase project identifier used by the server sender. |
| `Fcm:CredentialsFilePath` | Empty in appsettings. When Compose FCM is enabled, its default path is `/run/secrets/swiftaid-fcm-service-account.json`. |
| `FirebaseWeb:Enabled` and public Firebase fields | Disabled/empty in appsettings. The web module requires complete public Firebase settings, including `FirebaseWeb:VapidKey`; these are client configuration, not server credentials. |

The current Compose file maps `NATS_NOTIFICATION_STREAM` and `NATS_NOTIFICATION_SUBJECT` to
`Nats:NotificationStream` and `Nats:NotificationSubject`. It maps `FCM_ENABLED`,
`FCM_PROJECT_ID`, and `FCM_CREDENTIALS_FILE_PATH` to server settings; the service-account JSON is
mounted as a Compose file secret whose host path comes from `FCM_CREDENTIALS_FILE_PATH`
(fallback `FCM_CredentialsFilePath`, default `../secrets/swiftaid-fcm-service-account.json`). Web public
settings use the `FIREBASE_WEB_*` names shown in Compose. The checked-in `.env.example` is a key
reference only; use protected local/operator secret storage for real values.

Before enabling notifications in an environment:

1. Apply a reviewed notification database migration. A migration for the current notification
   mappings was not present in the inspected tree, so the schema cannot yet be provisioned from
   those mappings alone.
2. Provision `SWIFTAID_NOTIFICATIONS` with a reviewed JetStream configuration whose subject
   capture includes the configured `Nats:NotificationSubject`. Preserve the existing stream when
   changing configuration; no safe update/recovery procedure is currently verified.
3. Give the Gateway only the publish and stream-inspection permissions it needs. The current
   `infra/nats.conf` includes notification publish and `STREAM.INFO` permissions; it does not
   define the stream or a notification consumer.
4. Enable `Nats:Enabled` only after the stream exists and its settings match the application
   configuration. Enable FCM separately only after protected credentials and provider behavior
   have been verified. Push is not required to read the in-app feed.
5. Verify the authorized feed, permission changes, outbox retry/exhaustion, stream outage and
   provider failure paths in the required Docker-backed backend tests and actual client journeys.

Those are operational gates, not completed or verified runbook steps. Do not copy credentials into
logs, tickets, appsettings examples, mobile assets or browser configuration.

## Privacy, retries and failure behavior

- PostgreSQL owns the notification item and durable delivery outbox. The current schema mappings
  constrain notification kind/resource/action and enforce one outbox row per item, but the
  migration is pending.
- Push tokens are protected at rest and deduplicated by a token fingerprint in source. Registration
  and revocation are actor-scoped. Token encryption-key lifecycle and live token refresh/revocation
  still require deployment/device verification.
- The worker rechecks current recipient authority before publishing and again before sending push.
  A suppressed stale item is acknowledged without sending; temporary authority/provider failures
  are retried. The current worker uses one-minute leases, batches up to 25, exponential retry delay
  capped at one hour, and the PostgreSQL outbox caps attempts at eight. These are source values,
  not operational test results.
- JetStream failure or an unavailable stream prevents broker acknowledgement and leaves work for
  retry. FCM may accept a message without proving delivery. Retrying an uncertain external send can
  produce duplicate provider delivery; internal occurrence uniqueness does not promise exactly-once
  external delivery.
- If the notification API or database is unavailable, clients should show a recoverable unavailable
  state and retry the authorized feed. A push or change hint is never an authorization grant.

## Source references

- Gateway routes and actor checks: `src/SwiftAid.Gateway/Controllers/Notifications/`.
- Gateway feed and delivery worker: `src/SwiftAid.Gateway/Notifications/`.
- Shared API DTOs: `src/SwiftAid.Contracts/Notifications/`.
- Notification contracts: `src/SwiftAid.Application/Notifications/`.
- PostgreSQL mappings and stores: `src/SwiftAid.Infrastructure/Persistence/Notifications/`.
- Case assignment producer: `src/SwiftAid.Infrastructure/Persistence/Processing/PostgresCaseCommandPersistence.cs`.
- JetStream and FCM adapters: `src/SwiftAid.Infrastructure/Messaging/JetStreamInAppNotificationPublisher.cs` and `src/SwiftAid.Infrastructure/Notifications/FcmPushNotificationSender.cs`.
- Web and mobile surfaces: `src/SwiftAid.Gateway/wwwroot/notifications/` and `src/SwiftAid.Mobile/Services/Notifications/`.
