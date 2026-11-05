import hashlib
import importlib
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT.parents[1] / 'scripts/ellai'))
from provision_native_models import verify
from provision_models import extract_spacy
from build import allowed


class ModelTests(unittest.TestCase):
    def test_same_size_model_corruption_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / 'model'
            file.write_bytes(b'approved')
            artifact = {'size': 8, 'sha256': hashlib.sha256(b'approved').hexdigest()}
            self.assertTrue(verify(file, artifact))
            file.write_bytes(b'corrupt!')
            self.assertFalse(verify(file, artifact))

    def test_linguistic_archive_cannot_escape_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            wheel = root / 'model.whl'
            with zipfile.ZipFile(wheel, 'w') as archive:
                archive.writestr('../escaped.py', 'bad')
            with self.assertRaisesRegex(ValueError, 'Unsafe'):
                extract_spacy(wheel, root / 'site')
            self.assertFalse((root / 'escaped.py').exists())

    def test_linguistic_archive_installs_distribution_metadata_and_model(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            wheel = root / 'model.whl'
            with zipfile.ZipFile(wheel, 'w') as archive:
                archive.writestr('en_core_web_sm/__init__.py', '# model')
                archive.writestr('en_core_web_sm-3.8.0.dist-info/METADATA', 'Name: en-core-web-sm')
                archive.writestr('unexpected.py', 'bad')
            extract_spacy(wheel, root / 'site')
            self.assertTrue((root / 'site/en_core_web_sm/__init__.py').exists())
            self.assertTrue((root / 'site/en_core_web_sm-3.8.0.dist-info/METADATA').exists())
            self.assertFalse((root / 'site/unexpected.py').exists())

    def test_build_context_excludes_credentials_and_virtual_environments(self):
        for name in ('infra/runpod/runtime.env', 'src/server.pfx', 'src/secrets/password.json',
                     'workers/.venv/lib/package.py', 'src/.env.local', 'infra/runpod/server.key'):
            self.assertFalse(allowed(Path(name)), name)
        self.assertTrue(allowed(Path('infra/runpod/runtime.env.example')))
        self.assertTrue(allowed(Path('src/SwiftAid.Gateway/Program.cs')))


if __name__ == '__main__':
    unittest.main()
