# SW-03-008 geographic edge-case test audit - 2 November 2026

## Outcome

Existing focused integration tests cover seed idempotence/provenance repair, unknown parent behavior, same-name region/settlement ancestry disambiguation, country scope and result caps. The same focused GeographicRegistryTests suite passed 7/7 earlier this turn (EV-037). The full requested matrix is incomplete: no dedicated alias/alternative spelling, offline/unknown draft, missing geometry across all kinds, hierarchy-change migration, or cross-scope authorization regression was identified in the reviewed test files. Keep SW-03-008 partial.

## Existing evidence

- `tests/SwiftAid.Integration.Tests/GeographicRegistryTests.cs`: 7 tests, focused command and result recorded in EV-037; uses in-memory SQLite.
- `tests/SwiftAid.Ui.Tests/LocationPickerTests.cs`: component search/browse/cascade/provisional interaction test sources exist; not run for this audit.
- `tests/SwiftAid.Ui.Tests/GeoPlaceDraftMapperTests.cs`: mapping/clear/unsupported-country test sources exist; not run for this audit.

## Gaps

Add focused cases for alias and alternate spelling without fuzzy misidentification, offline draft restoration, explicit unknown/manual missing geometry with no invented coordinates, parent/hierarchy corrections preserving stable IDs, and authorization isolation across country/workspace boundaries. Run PostgreSQL migration and browser/device behavior separately from in-memory tests. No tests were run for this audit.
