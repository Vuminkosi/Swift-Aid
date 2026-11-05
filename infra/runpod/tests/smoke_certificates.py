"""Run only in a disposable root Python container: exercises real OpenSSL and CA trust."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import start


def main():
    for user in ('postgres', 'swiftaid'):
        subprocess.run(['useradd', '--user-group', user], check=True)
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        root.chmod(0o755)
        start.STATE = root / 'state'
        start.RUN = root / 'run'
        start.RUN.mkdir(mode=0o755)
        for name in ('security', 'keys'):
            path = start.STATE / name
            path.mkdir(parents=True, mode=0o700)
            shutil.chown(path, user='swiftaid', group='swiftaid')
        supervisor = start.Supervisor()
        values = start.load_secrets(start.STATE / 'security/credentials.json')
        start.certificates(supervisor, values)
        persistent = ['ca.crt', 'ca.key', 'protection.pfx', 'identity-signing.pfx', 'identity-encryption.pfx']
        before = {name: hashlib.sha256((start.STATE / 'security' / name).read_bytes()).hexdigest() for name in persistent}
        start.certificates(supervisor, values)
        after = {name: hashlib.sha256((start.STATE / 'security' / name).read_bytes()).hexdigest() for name in persistent}
        assert before == after, 'Restart rotated persistent identity or keyring certificates'
        subprocess.run(['openssl', 'verify', '-verify_hostname', 'localhost',
                        str(start.STATE / 'security/server.crt')], check=True)
        env = dict(os.environ, CERT_PASSWORD=values['certificate_password'])
        for name in ('protection.pfx', 'identity-signing.pfx', 'identity-encryption.pfx', 'server.pfx'):
            supervisor.run('Verify PKCS12', ['openssl', 'pkcs12', '-in', str(start.STATE / 'security' / name),
                           '-passin', 'env:CERT_PASSWORD', '-noout'], env, 'swiftaid')
        assert (start.RUN / 'postgres-tls/server.key').stat().st_mode & 0o777 == 0o600
        print('PASS: TLS trust, hostname, readable PKCS12, and stable identity/keyring certificates across restart')


if __name__ == '__main__':
    main()
