"""Download only during boot; inference services subsequently run offline."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import urllib.request
import zipfile

sys.path.insert(0, "/opt/tts")
from provision_native_models import verify


def extract_spacy(wheel: Path, destination: Path) -> None:
    with zipfile.ZipFile(wheel) as archive:
        for entry in archive.infolist():
            relative = Path(entry.filename)
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError("Unsafe linguistic model archive")
            if relative.parts[0] not in {"en_core_web_sm", "en_core_web_sm-3.8.0.dist-info"}:
                continue
            target = destination / relative
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    root = Path(os.environ["SWIFTAID_STATE"]) / "models"
    root.mkdir(parents=True, exist_ok=True)
    native = json.loads(Path("/opt/tts/native-models.lock.json").read_text())
    extra = json.loads(Path("/opt/runpod/models.lock.json").read_text())
    from huggingface_hub import hf_hub_download

    for model in native["models"] + extra["models"]:
        folder = root / model["model"].split("/")[1] / model["revision"]
        for artifact in model["files"]:
            classification = model["model"] == "facebook/bart-large-mnli"
            arguments = dict(repo_id=model["model"], filename=artifact["name"],
                             revision=model["revision"])
            if classification:
                arguments["cache_dir"] = str(root / "hf" / "hub")
                try:
                    target = Path(hf_hub_download(**arguments, local_files_only=True))
                except OSError:
                    target = Path("/nonexistent")
            else:
                arguments["local_dir"] = str(folder)
                target = folder / artifact["name"]
            if verify(target, artifact):
                continue
            if args.verify_only:
                raise RuntimeError(f"Missing or invalid cached model: {model['model']} / {artifact['name']}")
            if shutil.disk_usage(root).free < artifact["size"] + 3 * 1024**3:
                raise RuntimeError("Insufficient model storage including 3 GiB reserve")
            print(f"Downloading and verifying {model['model']} / {artifact['name']}", flush=True)
            target = Path(hf_hub_download(**arguments, force_download=target.exists()))
            if not verify(target, artifact):
                raise RuntimeError("Downloaded model failed its pinned integrity check")

    asset = extra["spacy"]
    wheel = root / asset["filename"]
    valid = wheel.is_file() and wheel.stat().st_size == asset["size"]
    valid = valid and hashlib.sha256(wheel.read_bytes()).hexdigest() == asset["sha256"]
    if not valid:
        if args.verify_only:
            raise RuntimeError("Missing or invalid English linguistic model")
        partial = wheel.with_suffix(".partial")
        with urllib.request.urlopen(asset["url"], timeout=120) as source, partial.open("wb") as output:
            shutil.copyfileobj(source, output)
        if partial.stat().st_size != asset["size"] or hashlib.sha256(partial.read_bytes()).hexdigest() != asset["sha256"]:
            raise RuntimeError("Linguistic model failed its pinned integrity check")
        partial.replace(wheel)
    extract_spacy(wheel, root / "spacy-site")
    print("All required local models verified; inference will use offline mode.", flush=True)


if __name__ == "__main__":
    main()
