# SW-04-003/004 report capture and subject schema audit - 2 November 2026

## SW-04-003 — partial

Public report contracts carry optional narrative, repeated observations and original-audio attachment references; captured/occurrence times and unknown/not-recorded states are separate. The Mobile guest path stores original audio locally/encrypted. No diagnosis field is required by the public report draft. However local guest transport is unavailable, so this does not prove server upload or end-to-end voice capture/replay.

Evidence: `src/SwiftAid.Contracts/Intake/IntakeSubmission.cs`, `src/SwiftAid.UI/Components/Public/PublicReportDraft.cs`, public report form/stepper, and Mobile `EncryptedGuestAudioStore.cs`.

## SW-04-004 — partial

The guest contract distinguishes `Human`, `Individual`, `Herd`, `Flock` and `OtherGroup` scope values, but the inspected `IntakeSubmission` does not carry a structured animal species plus at-risk/affected/dead counts and count provenance/source/precision fields. The managed case report may have richer subject contracts; that does not establish an equivalent guest intake schema. Do not infer clinical diagnosis or species/count data from narrative.

Evidence: `src/SwiftAid.Contracts/Intake/IntakeSubmission.cs`; current guest draft fields in `PublicReportDraft.cs`.

## Verification boundary

No source or tests were changed/run for this audit. Need a schema crosswalk for human individual versus animal individual/group, species, count presence/unknown/zero semantics and source/precision; validate it through the public receipt and authorized promotion path. Verify original voice remains distinct from optional transcription and never implies diagnosis. Mobile audio remains local-only until an authorized upload path exists.
