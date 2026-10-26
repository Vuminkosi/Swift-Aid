# Specification alignment and decision ledger

Snapshot date: 26 October 2026. This ledger records approved contracts and open implementation clarifications; it is not proof that software behavior exists.

## Authoritative pair

- Product specification: [spec.md](../spec.md), v1.3, SHA-256 `4317a3348a9571dac115d5cd41429a2535f246fc9d3f059a8b4cb2e37a1ee4a7`.
- Implementation plan: [implementation.md](../implementation.md), v2.3, SHA-256 `d720d77df550c5b3141c759ac5addfc809d98a1df85a131637202884320b1062`.
- Product behavior and `SA-*`, `AT-*`, and `PC-*` definitions belong to spec.md. Task contracts and implementation dependencies belong to implementation.md.
- The user's attached implementation instructions are supplemental and distinct from the two root source documents. The accepted UI decision below comes directly from the user's request dated 2026-10-26.

## Owner decision: shared UI framework

| Decision | State | Actor/date | Decision | Scope and evidence |
|---|---|---|---|---|
| UI-01 | accepted | Product owner, 2026-10-26 | MudBlazor is the primary/default Blazor UI library and shared design system for Android MAUI Blazor Hybrid and web surfaces. Syncfusion is a targeted secondary library only for justified complex specialized controls. MudBlazor owns shared design tokens and visual identity. Syncfusion use is subject to version compatibility, a valid license/eligibility, Android/web interaction, styling, accessibility and performance evidence. | Implements the implementation plan's UI component policy and the user's explicit addition. It does not change product requirements or authorize a specific Syncfusion component. No verified package compatibility, license, Android rendering or web rendering evidence is recorded yet. |

## Proposed product clarifications

These eight product clarifications remain proposed. No clinical or institutional policy approval is inferred from this ledger.

| ID | Conservative default in source | Owning tasks | State |
|---|---|---|---|
| PC-01 | Conservative worker/officer action permissions; request rather than grant reassignment/closure; explicit authority for official classification/publication | P1.2, P5.1, P5.3, P9.2, P10.1 | proposed |
| PC-02 | Minimal intake with explicit unknowns and observation/narrative/original-voice evidence; diagnosis not required | P1.1, P2.2, P3.1, P8.2, P11.1 | proposed |
| PC-03 | Expandable timeline command grouping with inspectable events and separate pending activity | P5.3, P11.1, P11.2 | proposed |
| PC-04 | Six core mobile and six officer modules with supporting views/panels; illustrations do not set authority | P11.1, P11.2 | proposed |
| PC-05 | Operational metric definitions and proposed device measurement targets; targets are not measured results or service guarantees | P8.4, P11.2, P12.1, P12.3 | proposed |
| PC-06 | Worker-documented receipt names reporter and confirmation method; no implied digital signature | P5.1, P8.3 | proposed |
| PC-07 | Resolved cases are history-first; reopen for new operational work; permitted corrections preserve closure | P1.1, P5.3, P11.1 | proposed |
| PC-08 | Same authorized demo officer may draft and publish only with explicit recorded review; never claim independent review | P7.3, P10.1, P11.2 | proposed |

A PC may move to `accepted_for_demo`, `rejected`, or `superseded` only when the actual decision, actor, date, reason, and affected tests are recorded. Until then, use only the labelled conservative demo behavior described by the source. The state `proposed` does not itself block unrelated tasks.

## Pair-level discrepancy and migration note

The previous `docs/CHANGELOG.md`, `docs/DOCUMENT-INTEGRITY.md`, and `docs/document-validation.json` described spec 1.2 / plan 2.2. Their historical counts, hashes, and 41-check result do not validate the current pair. The current source explicitly reports 350 requirement IDs, 72 acceptance scenarios, and 43 plan tasks. The current documentation records are a new snapshot; they do not alter product behavior.

## Source crosswalk discrepancy: P0.3 acceptance evidence

The v2.3 P0.3 task row lists AT-45 as accountable acceptance evidence. In spec v1.3, AT-45 is the optional open-weight bonus scenario; the plan separately identifies AT-14, AT-34, and AT-36 plus a real provider call for Meta capability verification. Preserve both source statements pending owner/task clarification. Do not promote the optional open-weight path into the core product requirement or block unrelated implementation on it. Affected task: P0.3. Status: clarification required; acceptance mapping NOT VERIFIED. P0.1 / AT-36 is also NOT VERIFIED.

No other product-requirement discrepancy is approved by the owner instruction. The UI framework choice is an implementation decision only.