"""Real initialization, TLS, least-privilege roles and persistence in a disposable PostgreSQL 17 image."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import start


def main():
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        root.chmod(0o755)
        start.STATE = root / 'state'
        start.RUN = root / 'run'
        start.RUN.mkdir(mode=0o755)
        for name in ('security', 'keys', 'postgres'):
            path = start.STATE / name
            path.mkdir(parents=True, mode=0o700)
            user = 'postgres' if name == 'postgres' else 'swiftaid'
            shutil.chown(path, user=user, group=user)
        values = start.load_secrets(start.STATE / 'security/credentials.json')
        supervisor = start.Supervisor()
        try:
            start.certificates(supervisor, values)
            start.database(supervisor, dict(os.environ), values)
            migrate = dict(os.environ, PGPASSWORD=values['database_migrate'], PGSSLMODE='verify-full',
                           PGSSLROOTCERT=str(start.STATE / 'security/ca.crt'))
            app = dict(migrate, PGPASSWORD=values['database_app'])
            command = ['psql', '-h', 'localhost', '-d', 'swiftaid', '-v', 'ON_ERROR_STOP=1']
            supervisor.run('Create migration-owned table', command + ['-U', 'swiftaid_migrate'], migrate,
                           data='CREATE TABLE pod_smoke(id integer PRIMARY KEY);')
            supervisor.run('Runtime table access', command + ['-U', 'swiftaid_app'], app,
                           data='INSERT INTO pod_smoke VALUES (1);')
            try:
                supervisor.run('Forbidden runtime schema change', command + ['-U', 'swiftaid_app'], app,
                               data='CREATE TABLE must_not_exist(id integer);')
            except RuntimeError:
                pass
            else:
                raise AssertionError('Runtime role unexpectedly has schema creation privileges')
            supervisor.close()
            supervisor = start.Supervisor()
            start.database(supervisor, dict(os.environ), start.load_secrets(start.STATE / 'security/credentials.json'))
            result = subprocess.run(command + ['-U', 'swiftaid_app', '-Atc', 'SELECT count(*) FROM pod_smoke'],
                                    env=app, check=True, capture_output=True, text=True)
            assert result.stdout.strip() == '1', 'Data did not persist across supervisor restart'
            print('PASS: PostgreSQL TLS, role separation, idempotent provisioning, and persisted data across restart')
        finally:
            supervisor.close()


if __name__ == '__main__':
    main()
