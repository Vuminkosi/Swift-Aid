# Architecture and retained topology audit — 2 November 2026

## Source mapping

| Responsibility | Current source | Evidence boundary |
|---|---|---|
| Android-first native client | `src/SwiftAid.Mobile/SwiftAid.Mobile.csproj` targets `net10.0-android` and `net10.0-ios` with `UseMaui=true`; `MauiProgram.cs` registers `AddMauiBlazorWebView()`. | Android Release build passed in the current baseline; no device install/interaction is claimed here. iOS package restore succeeded; no iOS build is claimed. |
| Responsive web/Gateway | `src/SwiftAid.Gateway` is an ASP.NET Core Web SDK project. `Program.cs` registers Razor Components with interactive server rendering and maps the application UI assembly. | Source and test evidence only; no current-browser journey is claimed. |
| Domain/application modular backend | `SwiftAid.Domain`, `SwiftAid.Application`, `SwiftAid.Contracts`, `SwiftAid.Infrastructure`, and `SwiftAid.Intelligence` are .NET 10 projects referenced by Gateway. | The full no-build test run passed its five test assemblies; this does not prove deployment topology. |
| Authoritative and supporting services | `infra/compose.yml` defines Gateway, PostgreSQL/PostGIS, MinIO, NATS, Redis and Qdrant. | The Compose manifest parses; it does not prove that every service is healthy or that persistence/restart workflows pass. |
| Python classification worker | `workers/pyproject.toml` requires Python 3.12. Compose defines `classification-worker` under the optional `classification` profile. It connects to NATS and the Gateway classification claim/completion API. | Worker tests passed in the baseline. The profile is optional; no worker-runtime or model-provider claim follows from the source definition. |
| Optional speech/model services | Compose defines `ellai-language` and `ellai-tts` under opt-in profiles. | Source configuration only; live provider/model/device capability remains separately gated. |

## Worker boundary clarification

`workers/src/swiftaid_workers/jobs/consumer.py` validates a strict job-envelope shape and explicitly does not execute or acknowledge jobs. The deployed Python consumer implemented in this checkout is the classification consumer. Do not describe the generic envelope module as a general-purpose worker runtime. Core Gateway-hosted .NET workers and outbox dispatchers remain separate components.

## Result

`SW-01-001` is confirmed at the source-topology level. Build/test and Compose-parse evidence is linked above. Service health, restart behavior, browser/device journeys, and live provider operation belong to separate checks and remain NOT VERIFIED unless their own evidence says otherwise.
