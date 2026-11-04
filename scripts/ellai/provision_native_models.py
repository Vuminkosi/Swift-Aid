"""Provision approved pinned models; never invoked by an inference request."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path


def verify(path, artifact):
    if not path.is_file() or path.stat().st_size != artifact['size']:
        return False
    digest = hashlib.sha256() if artifact['sha256'] else hashlib.sha1()
    if not artifact['sha256']:
        digest.update(f"blob {artifact['size']}\0".encode())
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest() == (artifact['sha256'] or artifact['gitBlob'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    manifest = json.loads(Path(__file__).with_name('native-models.lock.json').read_text())
    args.directory.mkdir(parents=True, exist_ok=True)
    pending = []
    for model in manifest['models']:
        folder = args.directory / model['model'].split('/')[1] / model['revision']
        for artifact in model['files']:
            if not verify(folder / artifact['name'], artifact):
                pending.append((model, folder, artifact))
    if args.verify_only:
        if pending:
            raise SystemExit(f'{len(pending)} artifacts missing or invalid')
        print('All pinned artifacts verified.', flush=True)
        return
    required = sum(a['size'] for _, _, a in pending)
    if shutil.disk_usage(args.directory).free < required + 3 * 1024**3:
        raise SystemExit('Insufficient disk space including 3 GiB reserve')
    from huggingface_hub import hf_hub_download
    for model, folder, artifact in pending:
        print(f"Downloading {model['model']} / {artifact['name']}", flush=True)
        target = folder / artifact['name']
        path = hf_hub_download(model['model'], artifact['name'], revision=model['revision'],
                               local_dir=folder, force_download=target.exists())
        if not verify(Path(path), artifact):
            raise SystemExit(f"Integrity check failed: {model['model']} / {artifact['name']}")
        print(f"Verified {artifact['name']}", flush=True)
    print('All pinned artifacts verified.', flush=True)


if __name__ == '__main__':
    main()
