# Capability and environment register

Snapshot date: 26 October 2026. Source: spec.md v1.3 and implementation.md v2.3. `NOT VERIFIED` means evidence is absent; it is not a pass or proof of provider failure.

| Capability / gate | Required state | Observed evidence | Status |
|---|---|---|---|
| .NET SDK | .NET 10 SDK selected by repository | Host reported SDK 10.0.403; root `global.json` now pins 10.0.403. Initial `dotnet --info` crashed while initializing Windows MSI workload installer. | SDK present; complete CLI/workload/build capability NOT VERIFIED |
| MAUI Android workload and device | P0.2 Release-mode native proof | No Android workload/device probe result recorded; no Android project observed in initial inventory. | NOT VERIFIED |
| ASP.NET Core / MudBlazor web render | P0.1 baseline | Gateway source project and central MudBlazor 9.11.0 package declaration observed. No restore/build or interactive browser render evidence. | IMPLEMENTED scaffold observed; NOT VERIFIED |
| Syncfusion component and license | Only when specialized control is justified | No package, component, license proof, or compatibility result recorded. | NOT PROVISIONED / NOT VERIFIED; gate remains closed |
| PostgreSQL/PostGIS | Authoritative persistence | No provisioned SwiftAid database or connection evidence recorded. | NOT PROVISIONED / NOT VERIFIED |
| Qdrant | Required scoped derived memory/cache infrastructure; PostgreSQL remains authority | No running/private Qdrant service, scoped retrieval, or invalidation evidence recorded. | NOT PROVISIONED / NOT VERIFIED |
| Facebook / Google / organization OIDC | Actual provider configuration and callback security | No registrations, secrets, callback tests, or live provider evidence recorded. | NOT PROVISIONED / NOT VERIFIED |
| Meta inference / ASR | Real endpoint and model capability probe per P0.3 | No live request, response-format, ASR, language, price, or balance evidence recorded. | NOT PROVISIONED / NOT VERIFIED |
| Python worker runtime | Locked environment and worker tests | Python 3.14.4 present on host; worker project/lock not verified in initial inventory. | Runtime present; worker NOT VERIFIED |
| Docker Compose | Local infrastructure reproducibility | Docker client/server 29.9.2 reported. No SwiftAid Compose config result recorded. | Engine present; project config NOT VERIFIED |
| Global reporting and routing | Account-free private public intake with explicit coverage state | Product contract in source; no running endpoint or journey evidence. | APPROVED CONTRACT; implementation NOT VERIFIED |
| Optional cross-sector inference | Explicit approval and validation gate | Spec says disabled pending separate decision/validation. | OFF by product contract |

Do not put credentials in this file. External integration, package compatibility, price, license, model, language quality, or clinical approval claims require their own evidence.
