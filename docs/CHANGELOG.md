# SwiftAid paired-document changelog

## 26 October 2026 — spec 1.4 / implementation plan 2.4

This source pair adds the bounded P0 P8.6 durable multi-turn investigation and clarification capability, with 15 SA-LOOP requirements and AT-73–AT-76. The full inventory is 365 unique SA-* requirements, 76 acceptance scenarios and 44 implementation tasks. P8.6 requires persistent PostgreSQL checkpoints, current-per-turn authorization/version checks, bounded loops and costs, approval/receipt reuse, scoped retrieval, and separate simulated-versus-real Meta evidence. Optional transcription and health-pattern analysis are not prerequisites; medical-advice routes and chain-of-thought persistence remain excluded.

The owner-selected UI decision remains MudBlazor primary/default for shared UI and design tokens, with Syncfusion only for justified complex specialized components subject to compatibility, licensing and Android/web evidence gates. This does not change other product requirements. Source review now confirms MudBlazor host wiring and zoom, while Android install/render/restart/offline proof remains NOT VERIFIED. The independently reviewed solution build/test snapshot (before P1.2A additions) passed with 0 warnings/errors and Domain 15 + Application 35 tests; other three assemblies reported no tests. Bootstrap passed 4/4 and worker checks passed 4 with 2 optional live NATS skips. P1.2A fixes and scoped tests passed, while one residual negative-test severity is DISPUTED, MASTER DISPOSITION PENDING; production authorization remains NOT VERIFIED. P0.1/AT-36, provider, license, device, and full release acceptance remain NOT VERIFIED. No P8.6 implementation or acceptance is claimed. The earlier transient bootstrap failure against a stale v1.3 assertion is superseded by the latest 4/4 run.

The previous docs baseline commit, the v1.3/v2.3 hashes, and the pre-metadata-correction v1.4/v2.4 hashes are retained as historical provenance in [DOCUMENT-INTEGRITY.md](DOCUMENT-INTEGRITY.md). A metadata-only correction aligned operative pair labels and inventory counts; no product requirements changed.

## 26 October 2026 — spec 1.3 / implementation plan 2.3

This pair applies the owner's product and access revision. It retains the complete support-system scope and adds account-free global private intake, guest offline continuity, Facebook/Google/organization OIDC identity, authenticated session subscriptions, and scoped Qdrant memory/cache. Historical event preparation is conditional and does not gate core implementation. Diagnosis, clinical/veterinary triage or advice, and generated health teaching remain excluded; optional cross-sector inference remains gated off.

The plan retains 37 original task IDs and adds six, for 43 total across phases 0–12. The current specification states 350 stable SA-* requirements and 72 acceptance scenarios. See the source pair for full definitions.

The owner additionally selected MudBlazor as primary UI and Syncfusion for justified complex specialized controls only. This is an implementation decision and does not change product requirements. docs/SPEC-ALIGNMENT.md records the decision and pending evidence.

The previous 1.2/2.2 integrity record and 41 document checks apply to that historical pair only. No product test/build or new full document-validation suite is claimed here.

## Historical pair: spec 1.2 / implementation 2.2

The prior document revision and its document-only checks remain available in repository history. Its reported hashes and 41-check result do not validate spec 1.3 / implementation 2.3.
