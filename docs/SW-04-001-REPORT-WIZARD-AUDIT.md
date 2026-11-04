# SW-04-001 report wizard audit - 2 November 2026

## Outcome

PASS at source level: the public manual report flow is implemented through the shared `PublicReportSurface` and `GuidedReportStepper`, hosted by Gateway `/report` and Mobile `GuestReport`. Staff managed reporting remains a distinct case-registration form. The guest feature is extended through the existing shared public wizard, not a second competing public wizard. This does not verify UI parity or a live submission journey.

## Evidence

- `src/SwiftAid.Gateway/Components/Pages/Report.razor` hosts `PublicReportSurface` and supplies registry/policy/submission clients.
- `src/SwiftAid.Mobile/Components/Public/GuestReport.razor` hosts the same `PublicReportSurface` for the guest path.
- `src/SwiftAid.UI/Components/Surfaces/Public/PublicReportSurface.razor` composes `GuidedReportStepper`.
- `src/SwiftAid.Mobile/Components/Pages/ManagedReport.razor` uses `CaseReportRegistrationForm` for authorized managed-case registration; it is a separate staff capability rather than a parallel anonymous intake wizard.

No tests, browser journey or device journey were run for this source audit. Continue tracking live intake and UI acceptance under SW-02-001 and SW-04-012.
