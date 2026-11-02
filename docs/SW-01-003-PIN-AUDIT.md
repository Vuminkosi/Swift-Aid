# SW-01-003 dependency and artifact pin audit - 2 November 2026

## Scope and outcome

Read-only audit of SDK, package, model and container pins, lockfiles, license records, and available vulnerability evidence. The checklist item remains **partial / blocked**: lockfiles and several model/image digests exist, but not all external images are immutable, rights and SBOM review remain incomplete, and current ownership handoffs for shared package/container files are unavailable (BL-005). No dependency, lockfile, or image reference was changed.

## Checks

| Area | Evidence and result |
|---|---|
| .NET SDK/packages | `global.json` selects SDK 10.0.403 with `rollForward: latestPatch`; central package versions and project `packages.lock.json` files are present. `dotnet restore SwiftAid.sln --locked-mode -m:1 -nr:false` PASS, as recorded in the baseline evidence. |
| .NET vulnerability query | `dotnet package list --project SwiftAid.sln --vulnerable --include-transitive --no-restore --format json` PASS, exit 0; JSON enumerated 16 projects and listed no vulnerable packages. This is the local query snapshot, not a full supply-chain or license audit. |
| Python | `workers/pyproject.toml` constrains Python to `>=3.12,<3.13`; `workers/uv.lock` exists. `uv lock --check` from `workers/` PASS (54 packages resolved). |
| JavaScript | Root package lock v3 and exact direct package versions are present. `npm ls --package-lock-only --depth=0` PASS for four declared packages. Playwright is absent, consistent with BL-003. |
| Models | Classification BART revision is pinned. Native model lock records five immutable revisions and artifact hashes; TTS model pin/hash and restrictions are documented. NLLB and MMS rights restrictions remain documented in [rights review](RIGHTS-REVIEW-2026-11-02.md). |
| Container images | Qdrant and nats-box references include digests. Compose also uses mutable `vuminkosimatibe/swiftaid-gateway:latest` and optional `ollama/ollama:latest`; several version-tagged upstream images have no digest. Local source-built images are separately recorded. |
| Rights and SBOM | Full transitive license/attribution, image vulnerability review, provenance, and SBOM remain NOT VERIFIED in [dependency inventory](DEPENDENCIES.md) and [rights register](RIGHTS-REGISTER.md). |

## Acceptance and follow-up

The lockfile checks establish reproducible package resolution for the checked snapshots, not bit-for-bit SDK installation or immutable container resolution. SW-01-003 cannot pass until image references are made immutable under the active owners, dependency and image inventories receive the required security/license/SBOM review, and outstanding model/data/asset rights are resolved. No migration is implicated by this audit. Keep gated model capabilities disabled until applicable rights and safety gates are satisfied.
