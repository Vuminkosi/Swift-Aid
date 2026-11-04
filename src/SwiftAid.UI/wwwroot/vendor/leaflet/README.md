# Vendored map dependencies

- Leaflet 1.9.4: https://leafletjs.com/download.html — BSD-2-Clause; see `LICENSE`.
- Leaflet.markercluster 1.5.3: https://github.com/Leaflet/Leaflet.markercluster/tree/v1.5.3 — MIT; see `MarkerCluster.LICENSE`.
- Assets retrieved from the corresponding versioned unpkg package URLs. No runtime CDN scripts are loaded.
- Leaflet JavaScript SHA-256: `db49d009c841f5ca34a888c96511ae936fd9f5533e90d8b2c4d57596f4e5641a`.
- Leaflet CSS SHA-256: `a7837102824184820dfa198d1ebcd109ff6d0ff9a2672a074b9a1b4d147d04c6`.

The map uses custom DOM markers, not Leaflet's default image markers. Tile imagery is separate from these libraries. The current street layer uses standard OpenStreetMap tiles with visible attribution, browser referrers and normal browser caching. It does not prefetch or download offline tile packs. Follow https://operations.osmfoundation.org/policies/tiles/; provision an appropriate tile service before high-traffic deployment. Automated map gesture tests block tile requests. Authorized report data is obtained from SwiftAid, never from the tile service.
