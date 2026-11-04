# SW-01-009 observability and abuse-control audit - 2 November 2026

## Outcome

Source review is partial. Selected privacy-aware metrics/traces, size bounds, a model-session token budget and a Facebook web-login Redis limiter exist. A general telemetry/export and trace-correlation setup, Gateway-wide rate-limit policies, and product-wide request/model budget controls were not established by this audit. This does not prove that all emitted logs are metadata-only or that secrets/payloads are absent from every telemetry path.

## Evidence

| Area | Source evidence | Finding / limit |
|---|---|---|
| Metrics and traces | `src/SwiftAid.Application/Abstractions/IngestionDispatchService.cs`; `src/SwiftAid.Gateway/Ellai/Voice/SileroVoiceMetrics.cs` | Ingestion defines an `ActivitySource`, bounded outcome/age/duration metrics and safe failure codes; voice metrics record inference duration and sequence gaps. No exporter or collector configuration was found in the reviewed Gateway/Compose paths. |
| Trace/correlation | `src/SwiftAid.Infrastructure/Persistence/Jobs/JobPersistenceEntities.cs` | Jobs carry correlation IDs, but no complete propagation path from HTTP request through command/outbox/worker was established. |
| Logging privacy | `src/SwiftAid.Gateway/Ellai/EllaiVoiceWebSocketEndpoint.cs`; messaging and worker logs | Sample voice logs use session/capture IDs and counts, while messaging logs generally use failure type/code. This targeted inspection does not prove all logs across all request paths exclude sensitive data. |
| Rate limits | `src/SwiftAid.Gateway/Program.cs`; `src/SwiftAid.Infrastructure/Identity/FacebookWebLoginRateLimiter.cs` | ASP.NET rate-limiter service/middleware and 429 rejection are registered; no general policy application was found. A dedicated Redis-backed Facebook web-login limiter exists and is independently configurable. |
| Payload bounds | Gateway Ellai/case import/command endpoints and `src/SwiftAid.Application/Attachments/AttachmentContracts.cs` | Several routes enforce request/audio/batch limits. A comprehensive per-route abuse/quota matrix was not found. |
| Model budget | `src/SwiftAid.Application/Ellai/EllaiUsageLedger.cs` | A session token budget exists. The source audit did not establish a universal provider-cost limit, reservation/reconciliation policy or usage dashboard for every model path. |
| Secrets | Compose injects provider/database credentials through configuration; appsettings contains empty configuration placeholders. | No secret values were read. Source review cannot establish secret-manager backing, rotation, or logs/export redaction in deployment. |

## Closure work

Define an approved low-cardinality telemetry contract and exporter, end-to-end trace propagation and redaction rules; add/verify applicable rate-limit policies at anonymous and expensive endpoints; map request/model/provider budgets and ambiguous-charge reconciliation; and audit structured log fields across sensitive flows. Validate with synthetic payloads in a safe test environment and confirm no clinical narratives, recordings, credentials, tokens or raw requests enter logs/metrics/traces.

No source or test was changed or run for this audit. Production telemetry, dashboards, secret-manager configuration and external billing behavior remain NOT VERIFIED.
