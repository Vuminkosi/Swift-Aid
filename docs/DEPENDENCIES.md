# Dependency and compatibility record

Source of truth for the intended stack and task graph: implementation.md v2.3 §§3–5, 19, 21. This file records observations, not a validated bill of materials.

## Intended architecture

Android-first .NET MAUI Blazor Hybrid client with native SQLite/Datasync local services; MudBlazor primary UI and conditional Syncfusion specialized controls; ASP.NET Core gateway and web surfaces; PostgreSQL/PostGIS authoritative persistence; private Qdrant for scoped derived memory/cache; NATS JetStream; Python workers; Microsoft AI/MCP integration with Meta-only permitted inference. The implementation plan defines responsibilities and dependency direction. Qdrant is not an authoritative case store; case data and grants remain in PostgreSQL.

## Observed baseline and open compatibility gates

| Item | Observed | Verification state |
|---|---|---|
| SDK | Root `global.json` pins .NET SDK 10.0.403; host reported that SDK installed. | CLI/workload state partly unverified; initial `dotnet --info` failed in Windows MSI installer initialization. |
| UI package | `Directory.Packages.props` declares MudBlazor 9.11.0. | No locked restore or MAUI/web compatibility result supplied. |
| Syncfusion | No package declaration or license proof observed. | Gated; package not selected. |
| Python | Host reports Python 3.14.4. | Worker dependency metadata/lock and tests not verified. |
| Container engine | Docker client/server reported 29.9.2. | Compose configuration and SwiftAid services not verified. |
| PostgreSQL/Qdrant/NATS | No service readiness evidence captured. | NOT PROVISIONED / NOT VERIFIED. |

The plan's candidate package versions are discovery notes, not a tested bill of materials. P0.1 must resolve compatible versions from actual targets, restore with locked dependencies, audit transitive licenses and record selected versions. P0.2 separately proves Android Release persistence/Datasync. P0.3 separately probes actual Meta capabilities. These gates do not authorize non-Meta inference, fabricated vectors, test doubles represented as live services, or treating package presence as capability proof.

See [TECHNICAL-SOURCES.md](TECHNICAL-SOURCES.md) for source pointers and `implementation.md` for the complete task dependency graph and compatibility references.
