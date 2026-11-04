# SW-03-003 shared location picker audit - 2 November 2026

## Outcome

Source review found a reusable MudBlazor `SwiftAidLocationPicker` using search-first interaction, 250 ms debounce, two-character minimum, ancestry context, retry feedback and a browse/provisional path. Backend search is indexed/scoped and capped. Component and contract test sources exist, but responsive/keyboard/screen-reader acceptance and full SwiftAidLocationPicker reuse across required surfaces were not checked in a rendered app. Keep SW-03-003 partial.

## Evidence

- `src/SwiftAid.UI/Components/Geography/SwiftAidLocationPicker.razor` renders a MudAutocomplete with a 250 ms debounce, search-first label/helper, ancestry for disambiguation, bounded query length, retryable error state, browse cascade and explicit provisional action.
- `src/SwiftAid.Infrastructure/Intake/Geography/GeographicRegistryService.cs` ranks/caps results, filters by country and returns stable IDs, country and ancestor context.
- `tests/SwiftAid.Ui.Tests/LocationPickerTests.cs` and `GeoPlaceDraftMapperTests.cs` cover component rendering and mapping. No tests were run for this audit.
- The picker appears in the guided report stepper; a complete usage inventory for organizations/facilities, sessions, filters, surveillance and analytics is not established here.

## Closure work

Run the component's focused tests and inspect the rendered picker at compact mobile, tablet and desktop sizes; verify keyboard selection/clear/retry/browse/provisional flow, focus and screen-reader labels; check out-of-order search responses and all required call sites. No source or test was changed or run for this inventory.
