"""Enrich the curated seed from explicitly supplied GeoNames SZ/ZW country dumps.

No fuzzy matching, case coordinates, network access, or ambiguity resolution.
Usage: python scripts/enrich-geographic-reference.py .codex/map-geography
"""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

root = Path(__file__).resolve().parents[1]
seed_path = root / "resources/catalogues/location-registry.southern-africa.seed1.json"
seed = json.loads(seed_path.read_text(encoding="utf-8"))
provenance = []
for country in ("SZ", "ZW"):
    archive = Path(sys.argv[1]) / f"{country}.zip"
    with zipfile.ZipFile(archive) as source:
        rows = [line.split("\t") for line in source.read(f"{country}.txt").decode("utf-8").splitlines()]
    matches_by_id = {}
    for place in seed["locations"]:
        if place["country_code"] != country:
            continue
        kind, name = place["location_type"], place["name"].casefold()
        candidates = [row for row in rows if row[8] == country
                      and ((kind.startswith("settlement") and row[6] == "P")
                           or (kind in ("region", "province") and row[7] == "ADM1")
                           or (kind == "country" and row[7] == "PCLI"))
                      and name in [row[1].casefold(), row[2].casefold(),
                                   *[alias.casefold() for alias in row[3].split(",")]]]
        if len(candidates) == 1:
            matches_by_id[place["id"]] = candidates[0]
    count = 0
    for place in seed["locations"]:
        row = matches_by_id.get(place["id"])
        if row is None:
            continue
        parent = matches_by_id.get(place.get("parent_id"))
        if place["location_type"].startswith("settlement") and parent is not None and parent[7] == "ADM1" and parent[10] != row[10]:
            continue
        place["latitude"], place["longitude"] = float(row[4]), float(row[5])
        place["source"] = f"https://www.geonames.org/{row[0]}/; CC BY 4.0"
        place["verification"] = "Unique country/type/name gazetteer match; public place reference, not surveyed case location"
        count += 1
    provenance.append({"country": country, "url": f"https://download.geonames.org/export/dump/{country}.zip",
                       "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "matched_records": count})
seed["coordinate_sources"] = provenance
seed_path.write_text(json.dumps(seed, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(json.dumps(provenance, indent=2))
