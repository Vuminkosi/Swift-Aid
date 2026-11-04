# SW-04-010 media lifecycle audit - 2 November 2026

## Outcome

The source keeps guest original audio local/encrypted and separates it from report acceptance, but guest upload is explicitly unsupported until the P2.4 upload-proof contract exists. Server-side intake can bind only a previously uploaded, submission-bound attachment; managed attachment transfer reauthorizes the actor and checks offsets/hash. No guest pending-upload/no-ASR end-to-end path was verified. Keep SW-04-010 partial/blocked on the missing guest upload contract.

## Evidence

- `src/SwiftAid.Application/Attachments/AttachmentContracts.cs` explicitly states `IAttachmentService` does not allow guest callers before the P2.4 upload-proof contract exists.
- `src/SwiftAid.Application/Intake/IntakeService.cs` validates and binds guest audio attachment IDs through `IIntakeAttachmentAuthority`; receipt/report acceptance is conditioned on valid binding.
- `src/SwiftAid.Mobile/Services/Intake/EncryptedGuestAudioStore.cs` and guest submission storage retain original audio locally and encrypted.
- `tests/SwiftAid.Integration.Tests/IntakeSubmissionAttachmentBindingTests.cs` is gated on a disposable PostgreSQL fixture and covers Uploaded→Attached atomicity, replay and duplicate binding. It was not run for this audit. It does not establish the missing guest upload capability or device behavior.
- The inspected guest report path has no upload transport; its Mobile intake transport remains unavailable/local-only. No ASR operation was triggered.

## Closure work

Define the P2.4 guest capability using submission-bound, expiring upload proof and strict byte/type/hash limits; ensure upload status is distinct from report receipt and preserve originals. Test pending/uploading/uploaded/failed states, retry and duplicate binding in a disposable PostgreSQL environment; verify no ASR/transcription before explicit authorization and policy confirmation. No data or service was changed for this audit.
