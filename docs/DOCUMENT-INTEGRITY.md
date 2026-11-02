# Paired-document integrity snapshot

## Current pair: 2 November 2026 — spec v1.5 / implementation v2.5

| Path | Version | Bytes | SHA-256 |
|---|---:|---:|---|
| `spec.md` | 1.5 | 227969 | `67cea198bad51abbb1b457a4a869e088a8b206521e6b67c430d0c2b8733ed365` |
| `implementation.md` | 2.5 | 603169 | `887f78cb23afb07f41ea6222a179db609bbd36f348a85782a6a3656bb48fe5dc` |

Static extraction found 379 unique `SA-*` identifiers and 379 individual priority-coded checklist rows, 86 acceptance scenarios and 86 scenario-owner rows, and 45 implementation task headings. All 379 source requirement IDs map to exactly one checklist row. The current pair's byte lengths and hashes were recomputed from root source files on 2 November 2026. These are document-structure checks; they do not verify application behavior, provider calls, device playback, rights, or deployment.

## Historical pair: 26 October 2026 — spec v1.4 / implementation v2.4

The following captured source hashes and verification evidence describe the historical v1.4/v2.4 pair only.

| Path | Version | Bytes | SHA-256 |
|---|---:|---:|---|
| `spec.md` | 1.4 | 217666 | `8900b57ea08c9db0704d26b19ab6289d4e4d7891d2609cc253caf1d3a66b4aee` |
| `implementation.md` | 2.4 | 430468 | `09f9eb36683d8860d2904cb1604c6a5c65e7ec9c51a9380e3bfef46a4a1f2023` |

The preceding v1.4/v2.4 snapshot was observed at source revision `ef8a8cd7d6a24897310bdf6490227082d675499a`: spec 217604 bytes / `8bdf616323a0d92149965b99d999f1ef235a2793812c6c87c4d203cf412ad60c`; implementation 430273 bytes / `2d98fd6ec3d9c019b87a4a9a4981485727b02cf695c88dbd028c1c0c13f7f76f`. The later update only corrected operative version/count metadata and current handoff labels; product requirements and acceptance scope were not changed.

## Historical source/document baseline

The prior docs baseline was `0bf4cf45abce753011803a80f029b45c8f8748dc` and described spec v1.3 / plan v2.3. Its captured hashes are retained as historical provenance: spec `4317a3348a9571dac115d5cd41429a2535f246fc9d3f059a8b4cb2e37a1ee4a7` (210012 bytes) and plan `d720d77df550c5b3141c759ac5addfc809d98a1df85a131637202884320b1062` (422038 bytes). Those hashes do not validate the current pair.

## Historical v1.4 source inventory

- Specification: 365 unique `SA-*` requirement identifiers and 76 `AT-*` acceptance scenarios.
- Implementation plan: 44 task headings across phases 0–12.
- Ledgers: 44 task rows, 76 scenario rows, and 76 explicit additive-family rows in the requirement table (61 existing added-family rows plus 15 SA-LOOP rows). The full 365-requirement inventory and base crosswalk remain defined by the source documents.

## Checks and evidence boundaries

Hash, byte-length and inventory extraction are static document checks. `docs/document-validation.json` records those checks and independently reported, scope-limited build/test/review evidence. The latest bootstrap run passed 4/4; the earlier transient failure is superseded. The integrated .NET test snapshot passed 50 tests across Domain and Application, while three assemblies reported no tests. P1.2A has a disputed residual test-severity finding and production authorization remains NOT VERIFIED. This documentation task did not run product builds or tests. No product release acceptance is claimed. Earlier 41-check results apply to an earlier document pair only.

## Historical source-label discrepancy

The preceding v1.4/v2.4 metadata-alignment record reported 365 requirements, 76 scenarios and seven additions for that historical pair. Historical references to the v1.3/v2.3 source basis remain intentionally labeled as historical; see `SPEC-ALIGNMENT.md`.

## Git visibility note

At the earlier snapshot, root source documents and existing documentation were present before implementation but ignored by tail entries in `.gitignore`; they were not tracked at the recorded baseline. The master reports that DevOps is authorized to remove the exact ignore entries under P0.1. This documentation task did not edit `.gitignore` or perform Git operations. Recheck tracked/ignored status before integration; file presence alone does not prove commit inclusion.
