# SwiftAid

SwiftAid is being implemented against the root-level product specification v1.4 and implementation plan v2.4. The current repository contains the .NET 10 solution and gateway shell, a MAUI Android project, Python worker package, and local Compose service definitions. This is an implementation in progress: buildable scaffolding and individual checks do not establish complete product acceptance.

The public reporting route is currently an account-free status page; report intake is not connected and it does not collect or submit a report. Education and public session pages currently contain no published entries. No AI-generated medical or veterinary diagnosis, treatment, or emergency dispatch is provided. The v2.4 plan adds P8.6 multi-turn reasoning, clarification, and durable checkpoints; this is newly specified work, not an implemented capability. Meta/provider access, live authentication and integrations, persistence behavior, and native-device acceptance remain subject to their documented gates.

## UI framework

MudBlazor is the primary Blazor UI library and shared design system for web and MAUI Blazor Hybrid. Syncfusion is reserved for a justified complex specialized control and remains gated by package compatibility, license eligibility, platform, accessibility, styling, and performance evidence. This framework decision does not change product requirements.

## Prerequisites

- .NET SDK 10.0.403, selected through `global.json`.
- Python 3.12 and [uv](https://docs.astral.sh/uv/) 0.13.5 or newer for the locked worker environment.
- Docker Compose v2 to validate or run the local service configuration.
- The .NET `maui-android` workload and Android build tools only for the Android Release build. The CI Android job installs the workload before compiling.

NuGet and Python package restore require access to their configured package feeds. Local checks do not call Meta or other product providers. No provider credentials are required for the repository checks.

## Build and verify

From the repository root, run the locked .NET restore, build, and test commands:

```powershell
dotnet restore SwiftAid.sln --locked-mode
dotnet build SwiftAid.sln --configuration Release --no-restore
dotnet test SwiftAid.sln --configuration Release --no-build --no-restore
python -m unittest discover -s tests/bootstrap -v
```

The solution contains the server-side projects and .NET tests. The MAUI Android project is built separately after its workload is installed:

```powershell
dotnet workload install maui-android --skip-manifest-update
dotnet restore src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --locked-mode
dotnet build src/SwiftAid.Mobile/SwiftAid.Mobile.csproj --configuration Release --framework net10.0-android --no-restore
```

Run the Python worker checks from the repository root:

```powershell
uv lock --project workers --check --python 3.12
uv sync --project workers --locked --all-groups
uv run --project workers --locked ruff check workers/src workers/tests
uv run --project workers --locked pytest workers/tests -q
```

The worker suite validates positive and negative JSON fixtures against all three managed-case command/event schemas, including the approved-review event link and request linkage. Its live NATS permission tests are skipped unless explicit test-broker credentials and URL are provided. Ordinary CI does not start a broker or call external product providers.

For a local Compose configuration check, first create a local `.env` from the safe template, then replace all placeholder passwords and API keys before starting any service:

```powershell
Copy-Item .env.example .env
docker compose -f infra/compose.yml config --quiet
```

Compose requires local environment values for its database, Qdrant, and separate NATS principals. `config --quiet` only validates the rendered configuration; it does not start containers. Do not commit `.env`; never reuse development credentials in deployed environments. The Compose file's service startup, data flows, and deployment behavior have not been established by configuration validation alone.

## Current product status

The exact implemented and pending scope is tracked in [docs/CAPABILITIES.md](docs/CAPABILITIES.md), [docs/PROGRESS.md](docs/PROGRESS.md), and [docs/TRACEABILITY.md](docs/TRACEABILITY.md). A successful build or test command covers only the code and checks it actually exercises; it is not evidence of provider access, authenticated journeys, deployed services, or Android device behavior.
