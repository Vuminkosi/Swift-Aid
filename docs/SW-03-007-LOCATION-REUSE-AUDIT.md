# SW-03-007 location selector reuse audit - 2 November 2026

## Outcome

Direct `SwiftAidLocationPicker` usage found in the shared `GuidedReportStepper` only. The Mobile guest report contains its own geography lookup/mapping code, and the Gateway report/surveillance pages do not directly instantiate the shared component in the inspected `.razor` source. The required reuse set also names organizations/facilities, community sessions, filters and analytics; shared-selector reuse was not found there. Keep SW-03-007 partial.

## Evidence

The reusable component source path is `src/SwiftAid.UI/Components/Geography/SwiftAidLocationPicker.razor`. The direct call site is:

- `src/SwiftAid.UI/Components/Public/GuidedReportStepper/GuidedReportStepper.razor`

`src/SwiftAid.Mobile/Components/Public/GuestReport.razor` has separate geo lookup and mapping methods; no direct shared picker component use was found. Gateway report and surveillance routes likewise did not show the shared component in their `.razor` markup.

The inspected organization (`Organizations.razor`), session editor/list, map/analytics and filter surfaces did not show a direct `SwiftAidLocationPicker` use. No UI tests or rendered journey were run for this inventory.

## Closure work

For each required surface, either reuse the shared selector where a place is selected or document why its location semantics are different. Keep map filters and analytics from converting administrative scope into case-level coordinates. Test stable IDs, ancestry, privacy/authorization, and responsive behavior at each actual call site.
