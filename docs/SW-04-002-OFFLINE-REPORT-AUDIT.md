# SW-04-002 offline guest/staff report audit - 2 November 2026

## Outcome

Source paths exist for encrypted local guest drafts/audio and a separate authenticated staff command outbox. The Mobile guest transport is currently unavailable/local-only; staff commands are tied to server workspace access and an outbox replay path. Previous device evidence covers first-run Welcome navigation, not an offline report draft on a fresh Android install. SW-04-002 remains partial.

## Evidence

- Guest: `src/SwiftAid.Mobile/Services/Intake/EncryptedGuestSubmissionStore.cs`, `EncryptedGuestAudioStore.cs`, `LocalOnlyGuestSubmissionCoordinator.cs`, and `UnavailableGuestIntakeTransport.cs` keep guest drafts local/encrypted and do not represent server acceptance.
- Staff: `src/SwiftAid.Client/Intake/GuestDraftStore.cs` is distinct from `src/SwiftAid.Client/Outbox/CommandOutbox.cs` and `OfflineCaseCommandClient.cs`; case commands require an authorized actor/workspace and are replayed through Gateway.
- Workspace state is sourced through `IWorkspaceAccessClient` and the Gateway membership/access client; the guest path does not require it.
- Prior Android fresh-install evidence recorded in `docs/PROGRESS.md` covers welcome onboarding only. No fresh Android offline report draft, force-stop, reboot or reconnect journey was found in the current evidence set.

## Closure evidence required

On a clean Android install, create human and animal guest drafts while offline, capture/store original audio, force-stop/reboot and restore, then reconnect and show the exact local versus server receipt state. Separately sign in as an authorized staff actor, select a verified workspace, queue a command and confirm replay/receipt. Verify guest work never enters the staff outbox or becomes case authority. No device test was run for this audit.
