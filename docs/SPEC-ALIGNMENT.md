# Specification alignment and decision ledger

Snapshot date: 26 October 2026. This ledger records approved contracts and open source discrepancies; it is not proof that software behavior exists.

## Current authoritative pair

- Product specification: [spec.md](../spec.md), v1.4, SHA-256 `8900B57EA08C9DB0704D26B19AB6289D4E4D7891D2609CC253CAF1D3A66B4AEE`.
- Implementation plan: [implementation.md](../implementation.md), v2.4, SHA-256 `09F9EB36683D8860D2904CB1604C6A5C65E7EC9C51A9380E3BFEF46A4A1F2023`.
- Current extracted inventories: 365 unique `SA-*` requirements, 76 `AT-*` scenarios, and 44 task headings. This update adds the P8.6/SA-LOOP/AT-73–AT-76 traceability; it does not verify the product.
- The user attachment remains supplemental input. The MudBlazor/Syncfusion decision below is the owner's explicit UI choice and does not change other product requirements.

## Historical pair and provenance

The prior documentation baseline was committed at `0bf4cf45abce753011803a80f029b45c8f8748dc` and described spec v1.3 / plan v2.3: spec SHA-256 `4317a3348a9571dac115d5cd41429a2535f246fc9d3f059a8b4cb2e37a1ee4a7`; plan SHA-256 `d720d77df550c5b3141c759ac5addfc809d98a1df85a131637202884320b1062`. Current versioned source pair supersedes those as the active requirements; old hashes/commit remain historical evidence and are retained in `DOCUMENT-INTEGRITY.md`.

## Owner decision: shared UI framework

| Decision | State | Actor/date | Decision | Scope and evidence |
|---|---|---|---|---|
| UI-01 | accepted | Product owner, 2026-10-26 | MudBlazor is the primary/default Blazor UI library and shared design system for Android MAUI Blazor Hybrid and web surfaces. Syncfusion is a targeted secondary library only for justified complex specialized controls. MudBlazor owns shared design tokens and visual identity. Syncfusion use is subject to version compatibility, a valid license/eligibility, Android/web interaction, styling, accessibility and performance evidence. | It does not change product requirements or authorize a specific Syncfusion component. Package compatibility, license, Android rendering and web rendering remain NOT VERIFIED. |

| UI-02 | accepted | Product owner, 2026-10-26 | Use the owner-provided SwiftAid loader GIF, with optional Lottie rendering through MudXtra's standalone `Blazor.Lottie.Player` package. This is an auxiliary animation renderer only; it does not replace UI-01, change the targeted Syncfusion rule, or alter other product requirements. | Package references, local runtime configuration, and GIF/Lottie/still-image assets are present in the current working tree; no current runtime rendering proof is claimed. GIF/Lottie creator and redistribution rights remain NOT VERIFIED. NuGet 1.0.6 reports MIT and .NET 8+; pinned repository LICENSE is MIT, while its source project declares 1.0.5. Exact package/source equivalence is NOT VERIFIED. See [UI-COMPONENTS.md](UI-COMPONENTS.md) and [RIGHTS-REGISTER.md](RIGHTS-REGISTER.md). |
| UI-03 | accepted | Product owner, 2026-10-26 | Use the root `swiftaid-icon.png` for theme colors, Gateway favicon, and Android application-icon source. | Current root/favicon/app-icon files match at SHA-256 `CA502C538415586ABE031CFCBA2B20EDF742E24002C7FB1B528F9DF5A9DF86BD`, 1,334,182 bytes, 1254×1254. Mobile launcher variants were generated in a reported Release build; device display and creator/license/redistribution rights remain NOT VERIFIED. |
| UI-04 | accepted | Product owner, 2026-10-26 | Share UI components across web and MAUI through a portable `SwiftAid.UI` Razor Class Library targeting `net10.0` with MudBlazor and Lottie; migrate shared loader/theme/card/status code into the library. Keep server SDKs/secrets out of UI code. | Implemented shared foundation: Gateway and Mobile reference `src/SwiftAid.UI`; the library contains shared theme provider/tokens, loader, page heading, body text, and service-boundary notice. Independent code and UX reviews and Gateway/Android Release builds are reported PASS. Host-specific pages/layout remain local; this is not proof every planned component has migrated. An earlier bounded public-shell Chrome journey passed Home/navigation, `/sessions` → `/report`, truthful unavailable states, the Mud shell, and 320px. Later corrected browser scripts were independently reviewed and rerun: the shared loader fixture covered GIF/Lottie DOMLoaded, reduced motion, theme switching, responsive widths, fallback, and the Gateway drawer mouse/keyboard state. The reruns confirm only those bounded component/navigation states; broader browser/product workflows, screen-reader/runtime accessibility, Android/device rendering, and full host migration remain NOT VERIFIED. Script hashes and precise scope are recorded in `UI-COMPONENTS.md`. |

## Owner-approved service implementation choices

| Decision | State | Actor/date | Decision | Scope and source alignment |
|---|---|---|---|---|
| INF-01 | accepted | Product owner, 2026-10-26 | MinIO is the private original-file/audio/video object-store choice; Redis provides backend application cache and exact-result cache with bounded TTL; Qdrant is limited to scope-filtered AI memory/search indexing; PostgreSQL remains authoritative; NATS JetStream carries queues/events; FCM push is optional; native SQLite holds offline read models/drafts; UI assets are bundled. Provider configuration is server-side at the Gateway. | These are infrastructure choices and do not change other product requirements. The implementation plan v2.4 §3.3 says no Redis is required; this accepted owner choice adds Redis for backend application caching. Preserve that source statement and record the mismatch rather than rewriting the specification/plan. See [SERVICES.md](SERVICES.md). Runtime/provider capabilities remain independently gated. |

## Proposed product clarifications

PC-01 through PC-08 remain proposed absent a recorded owner acceptance. No clinical or institutional policy approval is inferred.

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

## P8.6 approved scope addition

P8.6 is a P0 operational capability defined by spec v1.4 §15.8 and implementation v2.4. It covers durable structured multi-turn administrative reasoning and factual clarification; PostgreSQL owns run/checkpoint/turn state, while Qdrant is only a scoped retrieval/cache index. Each turn rechecks actor grants and case/source versions, uses existing NATS outbox/fencing/idempotency, is bounded by cycle/tool/budget limits, and reuses P8.3 approvals/receipts. User-visible questions and structured evidence/decision summaries are allowed; hidden chain-of-thought is never persisted or shown. Medical-advice routes remain excluded. Optional transcription and health-pattern analysis are not prerequisites.

| Contract item | Approved source requirement |
|---|---|
| Dependencies | P8.3, P8.5, P6.2, P1.2; acceptance dependency tracking does not block independent ready work. |
| Evidence | AT-73–AT-76 plus focused application/integration/Python checks and one real supported Meta multi-turn smoke with synthetic case data. Simulated and real provider calls must be distinguished. |
| Current status | P8.6 implementation, behavioral tests, restart/resume, provider smoke and UI states are NOT VERIFIED; no P8.6 result is claimed in this ledger. |

## Metadata-only source correction

The operative paired-revision text, current handoff examples, task counts, and final inventory summary were aligned to v1.4/v2.4 and 365 requirements, 76 scenarios, and seven additive tasks. This was metadata/count reconciliation only; no requirement behavior or acceptance scope changed. Historical references to the v1.3/v2.3 source basis remain intentionally identified as historical. Immediately before this metadata correction, the v1.4/v2.4 pair was observed at source revision `ef8a8cd7d6a24897310bdf6490227082d675499a`, with spec SHA-256 `8bdf616323a0d92149965b99d999f1ef235a2793812c6c87c4d203cf412ad60c` (217604 bytes) and implementation SHA-256 `2d98fd6ec3d9c019b87a4a9a4981485727b02cf695c88dbd028c1c0c13f7f76f` (430273 bytes). Current corrected hashes are in `DOCUMENT-INTEGRITY.md`.

## Source crosswalk discrepancy: P0.3 acceptance evidence

The v2.4 P0.3 task still lists AT-45 as accountable acceptance evidence. Spec v1.4 defines AT-45 as the optional open-weight bonus scenario; P0.3 separately specifies AT-14, AT-34 and AT-36 plus a real provider call for Meta capability verification. Preserve both statements pending task/owner clarification. Do not make optional open-weight execution a prerequisite for the core product. P0.1 / AT-36 remains NOT VERIFIED.

The current source pair is the authority for requirements. This ledger does not imply implementation, test, provider, device, license, clinical, rights, release, or deployment evidence.
