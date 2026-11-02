# Source and decision conflict register — 2 November 2026

## Active authority

The active source pair is `spec.md` v1.5 and `implementation.md` v2.5. The byte lengths and SHA-256 hashes were recomputed from the root files and are recorded in [DOCUMENT-INTEGRITY.md](DOCUMENT-INTEGRITY.md). The prior 31 October current-pair hashes were stale; historical v1.4/v2.4 entries remain labeled historical.

Static coverage check: the spec contains 379 unique `SA-*` identifiers, and the master checklist contains exactly 379 priority-coded SA rows. Every source identifier maps to one checklist row. The checklist rows retain their source priority labels, including the combined `P1/Gate` classification; this check does not validate implementation or acceptance status.

## Owner decision records

| Record | Current source state | Consequence |
|---|---|---|
| PC-01 through PC-08 | `proposed` in [SPEC-ALIGNMENT.md](SPEC-ALIGNMENT.md) | Do not report as owner-approved. Preserve conservative defaults and existing gates pending recorded owner acceptance. |
| PC-09 | Owner-approved 27 October 2026, subject to conditions recorded in [SPEC-ALIGNMENT.md](SPEC-ALIGNMENT.md) | Approval is limited to that intake-coordination decision and its conditions; it does not approve PC-01 through PC-08. |
| RD-01 through RD-04 | `Unanswered` in [RULES-DECISIONS.md](RULES-DECISIONS.md) | Organizer eligibility, cross-sector restrictions, event timing/provenance and open-weight bonus verification/prize stacking remain unresolved. Related gated claims remain disabled. |

## Supplied-document comparison

`SW-00-002` remains blocked: the task attachment directory contains the two execution directives, but not the 16 source documents the checklist says must be compared. No missing document content or approvals were reconstructed from inference. Resume that comparison when the source documents are supplied.

## Verification boundary

This register records source metadata, identifier coverage and the decision states written in the repository ledgers. It does not assert full source-document equivalence, owner acceptance, clinical or institutional approval, product behavior, provider readiness or runtime acceptance.
