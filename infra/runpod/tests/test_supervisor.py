"""Linux process and retained-state tests, runnable in the Python base image."""
import importlib
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


@unittest.skipUnless(sys.platform == 'linux', 'PID1 and ownership tests require the Linux container environment')
class SupervisorTests(unittest.TestCase):
    def setUp(self):
        self.module = importlib.import_module('start')
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.chown = patch.object(self.module.shutil, 'chown')
        self.chown.start()

    def tearDown(self):
        self.chown.stop()
        self.temporary.cleanup()

    def test_credentials_survive_restart_and_reject_missing_existing_database_credentials(self):
        path = self.root / 'security' / 'credentials.json'
        path.parent.mkdir()
        first = self.module.load_secrets(path)
        self.assertEqual(first, self.module.load_secrets(path))
        self.assertEqual(0o600, path.stat().st_mode & 0o777)
        (self.root / 'postgres').mkdir()
        (self.root / 'postgres/PG_VERSION').write_text('17')
        path.unlink()
        with self.assertRaisesRegex(RuntimeError, 'restore security'):
            self.module.load_secrets(path)
        self.assertFalse(path.exists())

    def test_corrupt_credentials_are_never_regenerated(self):
        path = self.root / 'credentials.json'
        path.write_text(json.dumps({'redis': 'invalid'}))
        before = path.read_bytes()
        with self.assertRaises(RuntimeError):
            self.module.load_secrets(path)
        self.assertEqual(before, path.read_bytes())

    def test_failed_child_stops_acceptance_and_shutdown_reaps_owned_children(self):
        supervisor = self.module.Supervisor()
        with patch.object(self.module, 'RUN', self.root):
            try:
                supervisor.start('long-running', [sys.executable, '-c', 'import time; time.sleep(30)'], dict(os.environ), 'root')
                supervisor.start('failed-service', [sys.executable, '-c', 'raise SystemExit(7)'], dict(os.environ), 'root')
                supervisor.children[-1][1].wait(5)
                with self.assertRaisesRegex(RuntimeError, 'failed-service exited'):
                    supervisor.check()
            finally:
                supervisor.close()
        self.assertTrue(all(child.poll() is not None for _, child in supervisor.children))

    def test_failed_migration_command_is_not_ignored(self):
        supervisor = self.module.Supervisor()
        with self.assertRaisesRegex(RuntimeError, 'Database migration failed'):
            supervisor.run('Database migration', [sys.executable, '-c', 'raise SystemExit(9)'])

    def test_setup_timeout_reaps_process_and_reports_controlled_error(self):
        supervisor = self.module.Supervisor()
        before = time.monotonic()
        with self.assertRaisesRegex(RuntimeError, 'startup deadline'):
            supervisor.run('bounded setup', [sys.executable, '-c', 'import time; time.sleep(30)'], timeout=1)
        self.assertLess(time.monotonic() - before, 5)


if __name__ == '__main__':
    unittest.main()
