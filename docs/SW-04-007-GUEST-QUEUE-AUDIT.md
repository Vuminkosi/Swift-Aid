# SW-04-007 guest submission queue audit - 2 November 2026

## Outcome

Source and focused client tests verify stable `ClientSubmissionId`, a frozen queued submission record with payload hash validation, retryable versus terminal failure states, receipt reconciliation and encrypted local audio persistence. Android force-stop/reboot and live ambiguous Gateway response recovery were not run; keep SW-04-007 partial.

## Verification

- `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~GuestSubmissionJourneyTests --verbosity minimal` — **PASS**, exit 0, 9 passed, 0 failed/skipped.
- `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~EncryptedGuestAudioPersistenceTests --verbosity minimal` — **PASS**, exit 0, 12 passed, 0 failed/skipped.

`GuestSubmissionJourneyTests` covers create → queue → submit → receipt, retained queue on retryable transport failure, `NeedsAttention` on terminal failure and repeated queue behavior. `EncryptedGuestAudioPersistenceTests` covers local audio hash/persistence and store recreation. Sources: `src/SwiftAid.Client/Intake/GuestSubmissionCoordinator.cs`, `GuestDraftStore.cs`, `src/SwiftAid.Mobile/Services/Intake/EncryptedGuestSubmissionStore.cs` and `EncryptedGuestAudioStore.cs`.

## Limits

These are client/local tests, not Android device, Gateway, server idempotency or production-store evidence. Force-stop/reboot, network ambiguity after server acceptance, real receipt lookup and byte-for-byte retry across app process death remain NOT VERIFIED.
