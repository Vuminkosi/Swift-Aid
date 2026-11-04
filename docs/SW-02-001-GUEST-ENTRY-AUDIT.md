# SW-02-001 guest entry and private intake audit - 2 November 2026

## Outcome

Source review confirms account-free entry surfaces and an anonymous Gateway intake API. Mobile guest drafts are encrypted/local-first. The Mobile DI currently binds guest submission to an unavailable transport and a local-only coordinator; this is honest local queueing, not live server submission. Browser/device acceptance and live Gateway intake are NOT VERIFIED for this audit. Keep SW-02-001 partial.

## Evidence

| Surface | Source evidence | Result / limit |
|---|---|---|
| Gateway public intake | `src/SwiftAid.Gateway/Endpoints/PublicIntakeEndpoints.cs`; `src/SwiftAid.Gateway/Intake/WebGuestIntakeService.cs`; public workflow page/layout under `src/SwiftAid.Gateway/Components/` | Intake-session/submission routes are explicitly anonymous and do not depend on social-login authorization. The service is a source path; no browser submission was performed. |
| Ellai guest support | `src/SwiftAid.Gateway/Endpoints/EllaiGuestEndpoints.cs`; `src/SwiftAid.Gateway/Ellai/EllaiGuestSessionIdentity.cs` | Guest endpoints are separately mapped; endpoint source exists. External AI/model behavior is gated and not verified here. |
| Mobile guest UI | `src/SwiftAid.Mobile/Components/Public/GuestReport.razor` | Guest report page handles local draft/history and distinguishes queued/received/needs-attention states. No sign-in requirement is shown in the page route source. Device navigation and accessibility remain NOT VERIFIED. |
| Local privacy and submission | `EncryptedGuestSubmissionStore.cs`, `EncryptedGuestAudioStore.cs`, `LocalOnlyGuestSubmissionCoordinator.cs`, `UnavailableGuestIntakeTransport.cs` in `src/SwiftAid.Mobile/Services/Intake/` | Draft/audio storage is encrypted and the current mobile submission path remains local-only/unavailable at the transport boundary. Do not describe this as successful server intake. |
| Account boundary | `src/SwiftAid.Gateway/Endpoints/PublicIntakeEndpoints.cs` and `IdentityEndpoints.cs` | Public intake is separate from identity routes. An intake receipt alone does not establish managed-case access, staff role or assignment. |

## Acceptance still required

Run fresh anonymous web and Android journeys with synthetic data: enter without account/social consent; save/restore draft; submit and distinguish local queue from server receipt; verify receipt grants no managed-case access; and verify auth transitions do not attach/delete guest work. Browser Playwright availability is blocked under BL-003; Android device acceptance is NOT VERIFIED. No real patient/farmer data or external notification was used.

No source or test was changed or run for this source audit.
