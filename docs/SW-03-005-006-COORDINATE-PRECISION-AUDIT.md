# SW-03-005/006 coordinate semantics and GPS audit - 2 November 2026

## SW-03-005 — partial

`src/SwiftAid.Contracts/Geography/LocationContext.cs` distinguishes `LocationSource` (Reporter, Device, Geocoder) and `LocationPrecision` (Exact, Approximate, Unknown). The curated seed carries source and verification text; `GeoSearchResult` has optional coordinates, but the UI picker option intentionally carries no raw coordinates. The seed documentation describes GeoNames values as public place references, not surveyed case locations. The model does not distinguish a verified case point from a locality centroid or administrative representative point with a dedicated coordinate-kind field, so the complete required provenance distinctions are not established.

## SW-03-006 — source-level pass

`PublicReportDraft.ToSubmission` sets occurrence coordinates and accuracy to null for manual/registry selection and for explicit unknown location. The intake domain validator rejects coordinates without valid accuracy and precision, rejects device source without coordinates, and rejects exact precision without coordinates. A source search of Mobile/UI/client `.cs` and `.razor` files found no `Geolocation` API or location-permission request. This supports the current no-required-GPS/no-fabricated-coordinate behavior at source level; it is not a device permission-prompt test.

No tests or device journey were run for this audit. Existing data records may have public catalogue coordinates; those must continue to display as approximate place references and must never be silently copied into a case occurrence point.
