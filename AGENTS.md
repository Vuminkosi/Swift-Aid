# SwiftAid repository guidance

## Source of truth

- Implement product behavior from `spec.md` v1.5 and task contracts from `implementation.md` v2.5.
- Treat the supplied documents as requirements, not evidence that a capability has been implemented or validated.
- Preserve task IDs, `SA-…` requirements, `AT-…` scenarios, offline behavior, privacy boundaries, and capability gates.
- MudBlazor is the primary Blazor UI library. Use Syncfusion only for justified complex controls after compatibility and license evidence; keep a usable MudBlazor path while that gate is unresolved.

## Architecture and safety

- Keep PostgreSQL authoritative for records, permissions, and memory provenance. Qdrant is a private derived index; never use it as the case database.
- Keep public intake separate from privileged managed-case commands. An intake receipt does not grant staff access or imply assignment.
- Never enable medical advice, veterinary advice, clinical triage, or AI-authored health education. Preserve original evidence and label unknown facts as unknown.
- Keep secrets out of source, examples, logs, mobile packages, and Qdrant payloads. Do not use real patient data in development or tests.
- Do not treat mock output, a successful build, or a UI screenshot as proof of a live provider, Android device, or clinical capability.

## Engineering workflow

- Read the relevant spec and implementation sections before changing behavior.
- Preserve existing work and coordinate ownership before editing shared contracts, migrations, package locks, or generated files.
- Keep changes focused and maintainable. Run the task's specified focused checks and record exact commands and results.
- Do not claim provider, device, browser, licensing, or deployment verification unless it ran against the corresponding real environment.
