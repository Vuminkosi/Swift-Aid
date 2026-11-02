# SW-00-002 source inventory and comparison - 2 November 2026

## Source set located

The owner identified repository `spec.md`, `implementation.md` and `designs/` as the source locations. The active authority pair is `spec.md` v1.5 and `implementation.md` v2.5. A recursive inventory found 25 Markdown files under `designs/`:

| Folder | Files |
|---|---|
| `designs/` | `checklist.md` |
| `designs/casedetails/` | `README.md`, `requirements.md` |
| `designs/community-sessions/` | `README.md`, `requirements.md` |
| `designs/data/` | `architeture.md`, `pipeline-requirements-data-classification.md`, `pipeline-requirements-surverlance-inteligence-loop.md`, `pipeline-requirements.md`, `proposal.md` |
| `designs/docs/` | `ui-ux-design-checklist.md` |
| `designs/ellai/` | `conversation-test-cases.md`, `ellai-client-native-translated(NLLB)-voice-processing1.0.md`, `ellai-client-native-translated(NLLB)-voice-processing1.1.md`, `ellai-client-voice-processing.md`, `interative-ellai-requirements.md`, `ui-tour-nav.md` |
| `designs/location/` | `requirements.md` |
| `designs/onboarding/` | `README.md`, `requirements.md` |
| `designs/Surveillance-and-Outbreak-intelligence/` | `requirements.md` |
| `designs/themes/` | `agentic-ui-agent-contract.md`, `agentic-ui-dark.md`, `agentic-ui-light.md`, `README.md` |

This inventory does not match the checklist's claim that it consolidates 16 source documents. No 16-item manifest or separate original attachment set was located. The comparison is therefore partial, not a claim that 16 unnamed documents were each individually reconciled.

## Authority and conflict reconciliation

- Current repository authority is `spec.md` v1.5 and `implementation.md` v2.5; integrity metadata and identifier mapping are in [DOCUMENT-INTEGRITY.md](DOCUMENT-INTEGRITY.md) and [SOURCE-CONFLICT-REGISTER.md](SOURCE-CONFLICT-REGISTER.md).
- Older v1.4/v2.4 references occur in historical/design materials, including `designs/ellai/ui-tour-nav.md`. They do not supersede the current pair. Versioned NLLB design material describes conditional/research processing; current rights review keeps NLLB non-production, optional and disabled absent separate clearance.
- Product safety constraints remain controlling: no clinical advice/triage or AI-authored health education; unknown facts remain unknown; synthetic demo data and consented recordings are required; PostgreSQL remains authoritative and Qdrant derived/private.
- Owner decisions PC-01 through PC-08 remain proposed; RD-01 through RD-04 remain unanswered. Existing ledgers identify the unresolved choices; this comparison makes no approval inference.
- Task-specific designs supplement the product pair where consistent. Contradictions or unverified claims remain gated and are tracked through [SPEC-ALIGNMENT.md](SPEC-ALIGNMENT.md), [RIGHTS-REVIEW-2026-11-02.md](RIGHTS-REVIEW-2026-11-02.md), and [RULES-DECISIONS.md](RULES-DECISIONS.md).

## Rights and consent search

The repository search scope covered `designs/`, `docs/`, `fixtures/`, `resources/`, `src/SwiftAid.Gateway/wwwroot/` and `src/SwiftAid.Mobile/Resources/`. It found consent requirements and rights-review/register documents, but no recording-consent register and no evidence establishing creator, license or redistribution permission for the tracked notification/UI assets described in the rights register. Requirements that recordings be consented are policy evidence, not proof of a specific speaker's consent. Details and consequences are in [rights review](RIGHTS-REVIEW-2026-11-02.md) and [rights register](RIGHTS-REGISTER.md).

## Status

SW-00-002 remains partial until the owner confirms whether the 25-file repository source set is the intended complete set or supplies the exact 16-item manifest. SW-00-008 remains blocked/partial until actual recording-consent and asset/source-rights evidence is recorded. No product or rights decision was inferred from file presence.
