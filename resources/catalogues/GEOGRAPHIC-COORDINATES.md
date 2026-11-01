# Public place coordinates

The curated registry contains 264 locations. Its coordinate enrichment adds 111 public reference points (29 Eswatini, 82 Zimbabwe); 153 locations remain without coordinates. These are place references, not individual case locations or surveyed administrative boundaries.

Source: [GeoNames country gazetteers](https://download.geonames.org/export/dump/), retrieved 2026-11-01, licensed [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). The seed's `coordinate_sources` records the exact country archive URLs and SHA-256 hashes. Each matched location carries its source GeoNames ID/link. Existing SwiftAid location IDs and parent relationships are preserved.

Reproduce with `python scripts/enrich-geographic-reference.py PATH_TO_COUNTRY_ARCHIVES`, supplying the pinned `SZ.zip` and `ZW.zip`. Matching requires a unique country, feature type and exact name or gazetteer alias. Where a parent administrative reference is available, its country subdivision code must agree. Ambiguous, unmatched and conflicting records remain unresolved; no fuzzy matching or patient/report coordinates are used.

The automatic Gateway migration command refreshes the bounded registry under its existing PostgreSQL deployment lock. Repeating the import produces no duplicate records. Source attribution is refreshed alongside coordinates. The original seed's statement that coordinates remain null until verified still applies to unmatched records; gazetteer matches do not establish clinical or reporting accuracy.
