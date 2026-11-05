"""Bounded build context for hosts whose Docker client traverses protected ignored directories."""
import argparse
from pathlib import Path
import subprocess
import shutil
import tempfile

ROOT = Path(__file__).resolve().parents[2]
EXCLUDED = {'bin', 'obj', '.venv', '__pycache__', 'node_modules', 'secrets', '.git', 'private-media', 'attachments'}
FILES = ['global.json', 'Directory.Build.props', 'Directory.Packages.props', 'SwiftAid.sln',
         'infra/nats.conf', 'scripts/ellai/native-models.lock.json', 'scripts/ellai/provision_native_models.py',
         'docs/ellai/ellai-system-instruction.md', 'docs/TECHNICAL.md',
         'infra/scripts/bootstrap-notification-stream.sh', 'infra/scripts/bootstrap-ingestion-stream.sh']
DIRECTORIES = ['src', 'resources', 'workers', 'infra/runpod', 'infra/ellai-tts']


def allowed(path):
    return (not any(part in EXCLUDED for part in path.parts)
            and path.suffix.lower() not in {'.env', '.pfx', '.key', '.pem', '.p12', '.pyc'}
            and not path.name.startswith('.env'))


def sources():
    for name in FILES:
        yield ROOT / name
    for name in DIRECTORIES:
        for directory, folders, files in (ROOT / name).walk():
            folders[:] = [folder for folder in folders if folder not in EXCLUDED]
            for filename in files:
                path = directory / filename
                if allowed(path.relative_to(ROOT)) and not path.is_symlink():
                    yield path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tag', default='swiftaid/runpod:local')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='swiftaid-runpod-build-') as directory:
        context = Path(directory)
        for path in sources():
            destination = context / path.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, destination)
        result = subprocess.run(['docker', 'build', '--progress=plain', '--target', 'runtime',
                                 '-t', args.tag, '-f', 'infra/runpod/Dockerfile', '.'], cwd=context)
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
