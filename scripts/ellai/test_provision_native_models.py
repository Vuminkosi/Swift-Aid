import hashlib
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('provision', Path(__file__).with_name('provision_native_models.py'))
provision = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provision)

class IntegrityTests(unittest.TestCase):
    def test_large_file_hash_and_size(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'weights'
            path.write_bytes(b'approved')
            artifact = {'size': 8, 'sha256': hashlib.sha256(b'approved').hexdigest()}
            self.assertTrue(provision.verify(path, artifact))
            path.write_bytes(b'modified')
            self.assertFalse(provision.verify(path, artifact))
            path.write_bytes(b'short')
            self.assertFalse(provision.verify(path, artifact))

    def test_git_blob_attests_small_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            path.write_bytes(b'{}')
            artifact = {'size': 2, 'sha256': None, 'gitBlob': hashlib.sha1(b'blob 2\0{}').hexdigest()}
            self.assertTrue(provision.verify(path, artifact))
            path.write_bytes(b'[]')
            self.assertFalse(provision.verify(path, artifact))
            path.unlink()
            self.assertFalse(provision.verify(path, artifact))

if __name__ == '__main__':
    unittest.main()
