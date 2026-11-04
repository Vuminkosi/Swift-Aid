# SW-02-009 guest-work account isolation audit - 2 November 2026

## Outcome

Guest drafts and encrypted media use separate guest-scoped stores/keys and are not bound to an authenticated staff actor in the inspected contracts. The browser regression suite includes cleanup-journal restart/version tests, but the local Playwright module is unavailable (BL-003). No real sign-in/account-switch/device workflow was run. Keep SW-02-009 partial.

## Evidence

- `src/SwiftAid.Client/Intake/GuestDraftStore.cs` and `GuestSubmissionCoordinator.cs` own guest draft/submission state independently of the managed case command outbox.
- `src/SwiftAid.Mobile/Services/Intake/EncryptedGuestSubmissionStore.cs`, `EncryptedGuestAudioStore.cs` and `GuestInstallKeyStore.cs` provide local encrypted guest data/key boundaries; sign-in does not by itself turn local guest IDs into server attachment or staff case authority.
- `src/SwiftAid.Mobile/Services/Authentication/MobileAuthenticationSession.cs` and `MobileLogoutService.cs` own authentication lifecycle separately from guest storage. No end-to-end proof was gathered that every logout/account/workspace transition preserves guest data while clearing protected staff data.
- Existing client/UI tests exercise guest draft/history, cleanup and storage behaviors; browser file `tests/SwiftAid.Browser.Tests/guest-storage.browser.cjs` includes restart and exact-version cleanup coverage. Browser execution remains blocked by BL-003; no tests were run for this audit.

## Closure work

With synthetic data on web and Android, create guest draft/audio, sign in as actor A, switch to actor B, expire/revoke B, sign out, and return to guest mode. Prove guest work remains locally available without being attached/submitted under either account, and protected staff projections/commands remain inaccessible to the wrong or expired actor. Verify cleanup and retake boundaries on-device and in the browser.
