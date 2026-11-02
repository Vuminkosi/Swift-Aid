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

The owner confirmed that `spec.md`, `implementation.md` and the Markdown files in `designs/` are the intended source set. This supersedes the checklist's historical count of 16; no separate 16-item manifest is required. Stable identifiers are now set-matched, but detailed design-local identifiers still need an applicability/canonical crosswalk before semantic reconciliation is exhaustive.

## Authority and conflict reconciliation

- Current repository authority is `spec.md` v1.5 and `implementation.md` v2.5; integrity metadata and identifier mapping are in [DOCUMENT-INTEGRITY.md](DOCUMENT-INTEGRITY.md) and [SOURCE-CONFLICT-REGISTER.md](SOURCE-CONFLICT-REGISTER.md).
- Older v1.4/v2.4 references occur in historical/design materials, including `designs/ellai/ui-tour-nav.md`. They do not supersede the current pair. Versioned NLLB design material describes conditional/research processing; current rights review keeps NLLB non-production, optional and disabled absent separate clearance.
- Product safety constraints remain controlling: no clinical advice/triage or AI-authored health education; unknown facts remain unknown; synthetic demo data and consented recordings are required; PostgreSQL remains authoritative and Qdrant derived/private.
- Owner decisions PC-01 through PC-08 remain proposed; RD-01 through RD-04 remain unanswered. Existing ledgers identify the unresolved choices; this comparison makes no approval inference.
- Task-specific designs supplement the product pair where consistent. Contradictions or unverified claims remain gated and are tracked through [SPEC-ALIGNMENT.md](SPEC-ALIGNMENT.md), [RIGHTS-REVIEW-2026-11-02.md](RIGHTS-REVIEW-2026-11-02.md), and [RULES-DECISIONS.md](RULES-DECISIONS.md).

## Rights and consent search

The repository search scope covered `designs/`, `docs/`, `fixtures/`, `resources/`, `src/SwiftAid.Gateway/wwwroot/` and `src/SwiftAid.Mobile/Resources/`. It found consent requirements and rights-review/register documents, but no recording-consent register and no evidence establishing creator, license or redistribution permission for the tracked notification/UI assets described in the rights register. Requirements that recordings be consented are policy evidence, not proof of a specific speaker's consent. Details and consequences are in [rights review](RIGHTS-REVIEW-2026-11-02.md) and [rights register](RIGHTS-REGISTER.md).

## Status

## Requirement and task identifier reconciliation — 2 November 2026

Automated read-only comparison of the current source files and master checklist found:

| Identifier set | Source count | Checklist/crosswalk count | Result |
|---|---:|---:|---|
| Stable `SA-*` product requirements across `spec.md`, `implementation.md`, and all 25 design Markdown files | 379 unique | 379 unique in `checklist.md` | Exact set match; no missing or checklist-only IDs. |
| `AT-*` acceptance scenarios | 86 unique | 86 unique in `checklist.md` | Exact set match. |
| Implementation task IDs `P*` | 45 implementation headings | 45 rows in `spec.md` §29 | Exact set match; each task row supplies its SA/AT scope. These IDs are not copied verbatim into the master checklist; traceability is through shared SA/AT identifiers. |
| `SW-*` execution checks | 171 unique in `designs/checklist.md` | 171 unique in root `checklist.md` | Exact set match. |

Root `checklist.md` and `designs/checklist.md` have the same phase count and the same SW, SA and AT identifier sets. Their content differs: the root file carries current execution evidence, statuses, audit links and later clarifications, while the design copy remains the original unchecked source checklist.

Two detailed design sources define 73 local screen/flow/failure identifiers outside the canonical SA/AT/task namespaces: community sessions (46: `CM-01..15`, `CD-01..22`, `CW-01..09`) and onboarding (27 `M-*`/`W-*` screen IDs). These identify UI surfaces, transitions and failure probes, not a separate approved product requirement namespace.

| Local ID group | Source-to-canonical mapping | Applicability decision |
|---|---|---|
| Community `CM-01..15`, `CW-01..09`, `CD-01..18`, `CD-22` | `designs/community-sessions/requirements.md` §12.1 cites `SA-SES-01..14`, `SA-SUB-01..07`, `SA-ACL-01..09`, `SA-KNW-02..12`, `SA-UX-01..09`, `SA-NOT-01..05`, and `SA-SEC-01..13` as applicable; the CD table specifies validation, authorization, conflict and truthful-state probes. | UI/negative-state guidance supplements current spec where consistent; it does not prove those behaviors implemented. |
| Community `CM-06..08`, `CW-06`, `CW-08`, `CD-19..21` | Conditional attendee/attendance, feedback, and session logistical follow-up; the source requires real consent, eligibility, responsible-person and outcome contracts. | Deferred unless supported by real authorized backend contracts. Do not map session logistical follow-up to managed-case `SA-FUP-*` or infer attendance/feedback capability. |
| Onboarding `M-ONB-*`, `W-ONB-*`, `M/W-AUTH-*`, `M/W-PRF-*`, `M/W-DONE-*`, `M-TOUR-01`, `W-PUB-01`, `W-ACC-01` | The source explicitly references old spec v1.4 / implementation v2.4 and has no stable SA cross-reference. Current candidate families are `SA-PUB-*` for account-free public entry, `SA-IDN-01..14` for identity/provider/session behavior, and `SA-UX-*` for state, access, accessibility and role/authority presentation. | Candidate mapping only. Each screen's controls, persistence and provider claims need comparison against current v1.5/v2.5 contracts; provider flows remain gated. Do not treat role preference as a grant or stale mock content as policy. |

Onboarding screen-level semantic mapping remains partial; community-local IDs have family-level mapping, with conditional features explicitly deferred. External-library `BB-*` citations and `PUB-*` fragments of canonical `SA-PUB-*` IDs are excluded from the local-design count.

PC-01 through PC-08 remain proposed and RD-01 through RD-04 remain unanswered. NLLB/commercial-use and surveillance gates remain subject to their rights and approval records. No product decision is inferred from source presence.

SW-00-002 source location is resolved. Stable requirement/scenario/task identifiers are set-matched; community design IDs are mapped at family level, while onboarding screen-level semantic reconciliation and broader source conflict review remain open. SW-00-008 remains blocked/partial until actual recording-consent and asset/source-rights evidence is recorded.
