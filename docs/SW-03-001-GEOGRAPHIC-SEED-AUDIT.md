# SW-03-001/002 geographic seed and reconciler audit - 2 November 2026

## Source inventory

`resources/catalogues/location-registry.southern-africa.seed1.json` is schema v1.0, registry version `southern-africa.seed1`, with 264 locations: 41 SZ, 94 ZW and 129 ZA. Its declared coverage is partial: all included first-level regions/provinces plus selected settlements, not all districts, wards, villages or growth points. Kinds include three countries, four regions, 19 provinces and 238 unclassified settlements. Records include stable IDs, parent IDs, source and verification text; the catalogue notes that coordinate values are public place references, not surveyed case points. 153 rows lack coordinates.

`resources/catalogues/GEOGRAPHIC-COORDINATES.md` documents the GeoNames CC BY 4.0 source, retrieval date, archive hashes, unique-match reconciliation and unresolved behavior. The importer at `src/SwiftAid.Infrastructure/Intake/Geography/GeographicRegistryImporter.cs` upserts the curated seed by ID, refreshes provenance and preserves unmatched missing coordinates. This does not make the partial settlement catalogue comprehensive or certify official boundary accuracy.

## Verification

Command: `dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --no-restore -m:1 -nr:false --filter FullyQualifiedName~GeographicRegistryTests --verbosity minimal`

Result: **PASS**, exit 0, 7 passed, 0 failed, 0 skipped. The suite uses in-memory SQLite (no live database/network service). Coverage includes 264-row import and idempotent repeat, coordinate/provenance refresh without duplicates, reimport repair, ranked search with ancestry and country/result scoping.

## Disposition

- SW-03-001 stays partial because settlement coverage is explicitly incomplete and coordinate/verification claims are limited to cited public place references.
- SW-03-002 passes for the tested seed importer: repeat import adds/updates no rows, reimport repairs source drift, and 153 missing coordinates remain absent. Live PostgreSQL deployment/migration behavior was not verified by this focused SQLite suite.
