# Shared-contract and non-destructive change control — 2 November 2026

## Canonical state observed

- Repository: `C:\DevOps\SwiftAidv2`
- Branch: `interactive-ellai`
- Current revision when recorded: `8f51876bd0e0bf59550ea8029c550c14a55b9064`
- `git worktree list --porcelain`: one worktree, the canonical checkout.
- Worktree was clean after the current documentation commits.

## Rules confirmed

- Keep the active branch and canonical checkout. Do not create/switch/rename/merge/rebase/delete branches or worktrees, force-reset, broadly clean, or push.
- Preserve user changes and stage only the approved task paths. Commit verified, scoped slices on the current branch; inspect staged paths/content first.
- Before shared contracts, migrations, package locks, or generated files are edited, confirm the current path owner from an active handoff. `docs/TASK-OWNERS.md` is a historical snapshot and does not reserve paths or assign current owners.
- Do not deploy, reset shared data, run destructive database tests, or alter unrelated services without explicit scope and a disposable environment where required.
- Keep test fixtures synthetic. Separate source/build proof from live provider, device, browser, deployment, and product acceptance evidence.

## Ownership boundary

The master orchestrator retains checklist, contract and integration coordination. Per-path implementation owners remain unassigned unless current handoffs say otherwise. This process confirmation does not release `BL-005`; SW-01-004 or later shared code work must refresh exact path ownership before editing.
