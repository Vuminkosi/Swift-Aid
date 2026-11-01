"""Build attributed hospital/clinic references from HOT national OSM GeoJSON archives.
Usage: python scripts/build-medical-center-catalogue.py SOURCE_DIRECTORY [--download]
"""
import argparse
import hashlib
import io
import json
import math
import re
import urllib.request
import zipfile
from pathlib import Path

COUNTRIES = {"SZ": "SWZ", "ZW": "ZWE"}


def points(coordinates):
    if len(coordinates) >= 2 and isinstance(coordinates[0], (int, float)):
        yield coordinates[:2]
    else:
        for child in coordinates:
            yield from points(child)


def build(directory, download):
    directory.mkdir(parents=True, exist_ok=True)
    facilities, sources, identities = [], [], set()
    for country, iso3 in COUNTRIES.items():
        url = f"https://production-raw-data-api.s3.amazonaws.com/ISO3/{iso3}/health_facilities/hotosm_{iso3.lower()}_health_facilities_osm_geojson.zip"
        path = directory / f"{iso3}.zip"
        if download:
            with urllib.request.urlopen(url, timeout=30) as response:
                raw = response.read(10_000_001)
            if len(raw) > 10_000_000:
                raise ValueError("Archive exceeds size limit")
            path.write_bytes(raw)
        raw = path.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            if sum(item.file_size for item in archive.infolist()) > 50_000_000:
                raise ValueError("Expanded archive exceeds size limit")
            metadata = json.loads(archive.read("metadata.json"))["properties"]
            features = json.loads(archive.read("health_facilities.geojson"))["features"]
        if metadata["license"] != "ODbL-1.0":
            raise ValueError("Unexpected source license")
        sources.append({"country": country, "url": url, "sha256": digest, "snapshot": metadata["oex:snapshot"]})
        for feature in features:
            p, geometry = feature["properties"], feature["geometry"]
            kind = p.get("amenity") if p.get("amenity") in ("hospital", "clinic") else p.get("healthcare")
            name = p.get("name")
            if kind not in ("hospital", "clinic") or not isinstance(name, str) or not name.strip():
                continue
            identity = p["id"]
            if not re.fullmatch(r"(node|way|relation)/[1-9][0-9]*", identity) or identity in identities:
                raise ValueError("Invalid or duplicated source identity")
            if p.get("adm0_pcode") != iso3:
                raise ValueError("Country boundary mismatch")
            if geometry["type"] not in ("Point", "Polygon", "MultiPolygon"):
                raise ValueError("Unsupported facility geometry")
            vertices = list(points(geometry["coordinates"]))
            if not vertices or any(not math.isfinite(v) for point in vertices for v in point):
                raise ValueError("Invalid coordinates")
            longitude = (min(p[0] for p in vertices) + max(p[0] for p in vertices)) / 2
            latitude = (min(p[1] for p in vertices) + max(p[1] for p in vertices)) / 2
            if not (-90 <= latitude <= 90 and -180 <= longitude <= 180):
                raise ValueError("Out-of-range coordinates")
            identities.add(identity)
            facilities.append({"id": identity, "country": country, "name": name.strip(), "type": kind.title(),
                "latitude": round(latitude, 7), "longitude": round(longitude, 7), "sourceUrl": url,
                "sourceSnapshot": metadata["oex:snapshot"], "sourceSha256": digest,
                "coordinateKind": "Mapped point" if geometry["type"] == "Point" else "Mapped facility grounds centre"})
    return {"version": 1, "source": "OpenStreetMap contributors / HOT", "license": "ODbL-1.0",
        "attributionUrl": "https://www.openstreetmap.org/copyright", "sources": sources,
        "coverage": "Named mapped hospitals and clinics; not a complete official facility register.",
        "facilities": sorted(facilities, key=lambda item: (item["country"], item["name"], item["id"]))}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_directory", type=Path)
    parser.add_argument("--download", action="store_true")
    args = parser.parse_args()
    catalogue = build(args.source_directory, args.download)
    target = Path(__file__).resolve().parents[1] / "src/SwiftAid.UI/wwwroot/maps/medical-centers-v1.json"
    target.write_text(json.dumps(catalogue, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for country in COUNTRIES:
        print(country, sum(item["country"] == country for item in catalogue["facilities"]))
