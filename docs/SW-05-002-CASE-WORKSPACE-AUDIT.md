# SW-05-002 authorized case workspace audit - 2 November 2026

## Outcome

Partial. Source contains an authenticated “my cases” Gateway endpoint and responsive case-list UI with search, workflow/country/date filters, newest/oldest sorting, pagination, case status, reported category, owner label, updated time, and mobile cards. The list is explicitly based on cases available to the signed-in worker. The shared workspace contract carries additional filters and action slots, but the current Gateway and Mobile clients do not implement the full contract or provide a next-action/activity history.

## Evidence reviewed

- `src/SwiftAid.Gateway/Endpoints/CaseQueueEndpoints.cs`: authenticated route, bounded optional limit, generic unavailable response, and queue access result mapping.
- `src/SwiftAid.Gateway/Cases/CaseWorkspaceGatewayClient.cs`: derives workspace from authorized “mine” queue; caps source at 100; search matches generated case reference/country/workflow; workflow/country/date filters and sort/page operate on that retrieved set. Sector, region, assignee, and reported-category filters are unsupported. Owner label is the constant “Assigned to you”; the row contains update time and aggregate version but no activity timeline or next action. A 100-row source set produces a truncation notice.
- `src/SwiftAid.Mobile/Services/Presentation/MobileCaseWorkspaceClient.cs`: authorized queue projection; caps fetch at 500 and returns unavailable when that cap is reached, preserving completeness rather than silently paging a partial set. Search matches workflow/country; workflow/country/date filters, sort, and page are supported; sector/region/assignee/category are unavailable. Mobile rows omit public reference, owner, category, region and next action.
- `src/SwiftAid.UI/Components/Surfaces/Operations/OperationsCaseQueue.razor`: MudBlazor desktop table and mobile cards, filters/quick filters, pagination, status/owner/update/category/location fields and empty/loading/error states.
- `src/SwiftAid.Client/Presentation/CaseWorkspaceContracts.cs`: page/query and row contracts include available actions, owner/category, update timestamp, and optional preview/location, but no explicit next-action or activity-list contract.
- `src/SwiftAid.UI/Components/Cases/CaseQueue.razor`: simpler assigned-case table for the officer-specific queue, with status/country/update only.

## Verification

Source inspection only. No new test command was run for this audit. Existing prior UI component baseline is not evidence of rendered browser or device behavior for this task.

## Not verified / gaps

- Authenticated cross-scope authorization behavior against live Gateway data and tenant/membership boundaries.
- Complete server-side filtering and pagination over datasets beyond each client's cap; Gateway can show only the most recent 100 source rows, Mobile fails closed at 500.
- Mobile parity for owner, category, location, next action and activity details.
- Actual next-action selection, activity timeline and freshness semantics.
- Rendered responsive layout, keyboard/screen-reader behavior, and browser/Android workflows.

## Conclusion

Implementation follow-up: Mobile now projects recorded sector/classification and supports both filters before pagination. Choices derive only from the authorized queue; unknown source sectors stay Unknown. Seven focused MobileCaseWorkspaceTests passed, and the full SwiftAid.Client.Tests suite passed 300/300 with no skips. Commands: `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~MobileCaseWorkspaceTests --verbosity quiet`; `dotnet test tests/SwiftAid.Client.Tests/SwiftAid.Client.Tests.csproj --no-build --no-restore -m:1 -nr:false --verbosity quiet`. This supersedes the earlier Mobile sector/category gaps above. Android rendering and live authorization remain NOT VERIFIED.

Keep SW-05-002 unchecked and PARTIAL. Close with focused authorized-scope and pagination tests, complete mobile field/action projection, and browser/device acceptance for both list surfaces.
