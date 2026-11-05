"""Single-Pod supervisor. No Docker socket, dependency bypass, or background shell jobs."""
from __future__ import annotations

import fcntl
import json
import os
from pathlib import Path
import pwd
import secrets
import shutil
import signal
import ssl
import subprocess
import tempfile
import time
import urllib.request

from config import connection_string, gateway_environment, native_profiles

ROOT = Path('/opt/runpod')
RUN = Path('/run/swiftaid')
STATE = Path(os.environ.get('SWIFTAID_STATE', '/workspace/swiftaid'))
SECRET_NAMES = ('certificate_password', 'database_app', 'database_migrate', 'redis', 'qdrant',
                'nats_gateway', 'nats_bootstrap', 'nats_worker', 'minio_root', 'minio_app',
                'classification', 'tts', 'language')


def private_file(path: Path, content: str, owner: str = 'swiftaid', mode: int = 0o600) -> None:
    descriptor, name = tempfile.mkstemp(prefix=path.name + '.', dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, 'w') as output:
            output.write(content)
        temporary.chmod(mode)
        shutil.chown(temporary, user=owner, group=owner)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def load_secrets(path: Path) -> dict[str, str]:
    if path.exists():
        values = json.loads(path.read_text())
        if set(values) != set(SECRET_NAMES) or any(len(v) != 64 or
                any(c not in '0123456789abcdef' for c in v) for v in values.values()):
            raise RuntimeError('Persistent credentials are invalid; restore the matching security backup')
        return values
    # Never replace missing credentials on an existing database.
    if (path.parent.parent / 'postgres' / 'PG_VERSION').exists():
        raise RuntimeError('Existing database has no credentials file; restore security from backup')
    values = {name: secrets.token_hex(32) for name in SECRET_NAMES}
    private_file(path, json.dumps(values), owner='root')
    return values


class Supervisor:
    def __init__(self):
        self.children: list[tuple[str, subprocess.Popen]] = []
        self.stopping = False

    def signal(self, *_):
        self.stopping = True

    def check(self, expected_exit=None):
        if self.stopping:
            raise InterruptedError('Shutdown requested')
        for name, child in self.children:
            if child is not expected_exit and child.poll() is not None:
                raise RuntimeError(f'{name} exited ({child.returncode}); stopping the complete stack')

    def start(self, name, command, env, user='swiftaid', cwd=None):
        self.check()
        account = pwd.getpwnam(user)
        child = subprocess.Popen(command, env=env, cwd=cwd, user=account.pw_uid,
                                 group=account.pw_gid, extra_groups=[], start_new_session=True)
        self.children.append((name, child))
        print(f'Started {name}', flush=True)

    def run(self, name, command, env=None, user='root', data=None, timeout=300, show_output=False):
        self.check()
        account = pwd.getpwnam(user)
        # Setup commands can contain credentials. Never print their argv, stdout or stderr.
        process = subprocess.Popen(command, env=env, stdin=subprocess.PIPE,
                                   stdout=None if show_output else subprocess.DEVNULL,
                                   stderr=None if show_output else subprocess.DEVNULL,
                                   user=account.pw_uid, group=account.pw_gid, extra_groups=[],
                                   start_new_session=True)
        try:
            deadline = time.monotonic() + timeout
            payload = data.encode() if data else None
            while True:
                self.check()
                try:
                    process.communicate(payload, timeout=1)
                    break
                except subprocess.TimeoutExpired:
                    payload = None
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f'{name} exceeded its startup deadline') from None
            if process.returncode:
                raise RuntimeError(f'{name} failed (exit {process.returncode}); startup stopped')
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
            if process.stdin and not process.stdin.closed:
                process.stdin.close()

    def wait(self, name, probe, timeout=180):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            self.check()
            if probe():
                print(f'Ready: {name}', flush=True)
                return
            time.sleep(1)
        raise RuntimeError(f'{name} did not become ready within {timeout}s')

    def close(self):
        (RUN / 'health.json').unlink(missing_ok=True)
        deadline = time.monotonic() + 75
        for name, child in reversed(self.children):
            try:
                os.killpg(child.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            if child.poll() is None:
                try:
                    child.wait(max(0.1, min(15, deadline - time.monotonic())))
                except subprocess.TimeoutExpired:
                    print(f'Forced shutdown: {name}', flush=True)
                    os.killpg(child.pid, signal.SIGKILL)
                    child.wait()


def http_ok(url, headers=None):
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers or {}), timeout=3) as reply:
            return reply.status == 200
    except (OSError, ValueError):
        return False


def command_ok(command, env=None, user=None):
    options = {}
    if user:
        account = pwd.getpwnam(user)
        options = dict(user=account.pw_uid, group=account.pw_gid, extra_groups=[])
    try:
        return subprocess.run(command, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              timeout=5, **options).returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def certificates(supervisor, values):
    security = STATE / 'security'
    ca = security / 'ca.crt'
    key = security / 'ca.key'
    if ca.exists() != key.exists():
        raise RuntimeError('Incomplete private CA; restore its matching backup')
    if not ca.exists():
        supervisor.run('Create internal CA', ['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes',
                       '-keyout', str(key), '-out', str(ca), '-days', '3650', '-subj', '/CN=SwiftAid Pod CA',
                       '-addext', 'basicConstraints=critical,CA:TRUE'])
    shutil.copyfile(ca, '/usr/local/share/ca-certificates/swiftaid-pod.crt')
    supervisor.run('Trust internal CA', ['update-ca-certificates'])
    leaf = security / 'server.crt'
    if not leaf.exists() or not command_ok(['openssl', 'x509', '-checkend', '2592000', '-noout', '-in', str(leaf)]):
        private_file(RUN / 'certificate.ext', 'subjectAltName=DNS:localhost,IP:127.0.0.1,IP:::1\nextendedKeyUsage=serverAuth\n')
        supervisor.run('Create service key', ['openssl', 'req', '-new', '-newkey', 'rsa:3072', '-nodes',
                       '-keyout', str(security / 'server.key'), '-out', str(RUN / 'server.csr'), '-subj', '/CN=localhost'])
        supervisor.run('Sign service certificate', ['openssl', 'x509', '-req', '-in', str(RUN / 'server.csr'),
                       '-CA', str(ca), '-CAkey', str(key), '-CAcreateserial', '-out', str(leaf), '-days', '365',
                       '-extfile', str(RUN / 'certificate.ext')])
    env = dict(os.environ, CERT_PASSWORD=values['certificate_password'])
    supervisor.run('Export service certificate', ['openssl', 'pkcs12', '-export', '-in', str(leaf),
                   '-inkey', str(security / 'server.key'), '-out', str(security / 'server.pfx'),
                   '-passout', 'env:CERT_PASSWORD'], env)
    # Data Protection has an independent, persistent certificate. Never rotate it with the TLS leaf.
    for name in ('protection', 'identity-signing', 'identity-encryption'):
        target = security / f'{name}.pfx'
        if not target.exists():
            if any((STATE / 'keys').iterdir()) or (STATE / 'postgres' / 'PG_VERSION').exists():
                raise RuntimeError(f'Existing state has no {name} certificate; restore it from backup')
            supervisor.run('Create persistent application certificate', ['openssl', 'req', '-x509', '-newkey', 'rsa:3072', '-nodes',
                           '-keyout', str(RUN / f'{name}.key'), '-out', str(RUN / f'{name}.crt'), '-days', '3650',
                           '-subj', f'/CN=SwiftAid {name}'])
            supervisor.run('Export persistent application certificate', ['openssl', 'pkcs12', '-export', '-in', str(RUN / f'{name}.crt'),
                           '-inkey', str(RUN / f'{name}.key'), '-out', str(target), '-passout', 'env:CERT_PASSWORD'], env)
            (RUN / f'{name}.key').unlink()
    for path in security.iterdir():
        path.chmod(0o600)
        if path.name not in {'credentials.json', 'ca.key', 'ca.srl'}:
            shutil.chown(path, user='swiftaid', group='swiftaid')
    # PostgreSQL must own its private key; all copies stay in private runtime directories.
    pg_tls = RUN / 'postgres-tls'
    pg_tls.mkdir(mode=0o700, exist_ok=True)
    shutil.chown(pg_tls, user='postgres', group='postgres')
    for name in ('server.crt', 'server.key'):
        private_file(pg_tls / name, (security / name).read_text(), 'postgres')
    minio_tls = RUN / 'minio-tls'
    minio_tls.mkdir(mode=0o700, exist_ok=True)
    shutil.chown(minio_tls, user='swiftaid', group='swiftaid')
    private_file(minio_tls / 'public.crt', leaf.read_text())
    private_file(minio_tls / 'private.key', (security / 'server.key').read_text())


def write_configs(values):
    security = STATE / 'security'
    nats = (ROOT / 'nats-base.conf').read_text().replace('port: 4222', 'host: 127.0.0.1\nport: 4222')
    nats = nats.replace('http: 8222', 'http: 127.0.0.1:8222').replace('/data/jetstream', str(STATE / 'jetstream'))
    nats = nats.replace('"$JS.API.STREAM.CREATE.SWIFTAID_NOTIFICATIONS",',
                        '"$JS.API.STREAM.CREATE.SWIFTAID_JOBS",\n"$JS.API.STREAM.INFO.SWIFTAID_JOBS",\n'
                        '"$JS.API.STREAM.CREATE.SWIFTAID_EVENTS",\n"$JS.API.STREAM.INFO.SWIFTAID_EVENTS",\n'
                        '"$JS.API.STREAM.CREATE.SWIFTAID_NOTIFICATIONS",')
    nats += f'\ntls {{ cert_file: "{security}/server.crt", key_file: "{security}/server.key" }}\n'
    private_file(RUN / 'nats.conf', nats)
    private_file(RUN / 'redis.conf', f'''bind 127.0.0.1
port 0
tls-port 6379
tls-cert-file {security}/server.crt
tls-key-file {security}/server.key
tls-ca-cert-file {security}/ca.crt
tls-auth-clients no
requirepass {values['redis']}
dir {STATE}/redis
appendonly yes
appendfsync everysec
''')
    private_file(RUN / 'qdrant.yaml', f'''storage:
  storage_path: {STATE}/qdrant
service:
  host: 127.0.0.1
  http_port: 6333
  grpc_port: 6334
  enable_tls: true
  api_key: {values['qdrant']}
tls:
  cert: {security}/server.crt
  key: {security}/server.key
telemetry_disabled: true
''')
    private_file(RUN / 'nginx.conf', '''worker_processes auto;
pid /run/swiftaid/live/nginx.pid;
error_log /dev/stderr warn;
events { worker_connections 1024; }
http {
  access_log off;
  client_body_temp_path /run/swiftaid/live/client-body;
  proxy_temp_path /run/swiftaid/live/proxy;
  map $http_upgrade $connection_upgrade { default upgrade; '' close; }
  server {
    listen 8080;
    client_max_body_size 100m;
    location = /pod-health/ready {
      proxy_pass http://127.0.0.1:8092;
    }
    location / {
      proxy_pass https://127.0.0.1:8443;
      proxy_ssl_verify on;
      proxy_ssl_trusted_certificate /etc/ssl/certs/ca-certificates.crt;
      proxy_ssl_name localhost;
      proxy_ssl_server_name on;
      proxy_http_version 1.1;
      proxy_set_header Host $host;
      proxy_set_header Upgrade $http_upgrade;
      proxy_set_header Connection $connection_upgrade;
      proxy_buffering off;
      proxy_read_timeout 3600s;
      proxy_send_timeout 3600s;
    }
  }
}
''')


def database(supervisor, env, values):
    directory = STATE / 'postgres'
    if not (directory / 'PG_VERSION').exists():
        supervisor.run('Initialize PostgreSQL', ['initdb', '-D', str(directory), '--auth-local=peer',
                       '--auth-host=scram-sha-256', '--encoding=UTF8', '--locale=C.UTF-8'], user='postgres')
    if (directory / 'PG_VERSION').read_text().strip() != '17':
        raise RuntimeError('PostgreSQL data version differs; explicit upgrade required')
    private_file(directory / 'pg_hba.conf', 'local all postgres peer\nhostssl all all 127.0.0.1/32 scram-sha-256\n'
                 'hostssl all all ::1/128 scram-sha-256\n', 'postgres')
    supervisor.start('postgres', ['postgres', '-D', str(directory), '-c', 'listen_addresses=127.0.0.1',
                     '-c', 'unix_socket_directories=/var/run/postgresql', '-c', 'ssl=on',
                     '-c', f'ssl_cert_file={RUN}/postgres-tls/server.crt', '-c', f'ssl_key_file={RUN}/postgres-tls/server.key'], env, 'postgres')
    supervisor.wait('postgres', lambda: command_ok(['pg_isready', '-h', '/var/run/postgresql', '-U', 'postgres'], user='postgres'))
    sql = ''
    for role, secret in [('swiftaid_migrate', 'database_migrate'), ('swiftaid_app', 'database_app')]:
        sql += f"SELECT 'CREATE ROLE {role} LOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='{role}')\\gexec\n"
        sql += f"ALTER ROLE {role} PASSWORD '{values[secret]}';\n"
    sql += "SELECT 'CREATE DATABASE swiftaid OWNER swiftaid_migrate' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='swiftaid')\\gexec\n"
    sql += "\\connect swiftaid\nCREATE EXTENSION IF NOT EXISTS postgis;\nREVOKE CREATE ON SCHEMA public FROM PUBLIC;\n"
    sql += "GRANT USAGE ON SCHEMA public TO swiftaid_app;\n"
    sql += "ALTER DEFAULT PRIVILEGES FOR ROLE swiftaid_migrate IN SCHEMA public GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO swiftaid_app;\n"
    sql += "ALTER DEFAULT PRIVILEGES FOR ROLE swiftaid_migrate IN SCHEMA public GRANT USAGE,SELECT ON SEQUENCES TO swiftaid_app;\n"
    supervisor.run('Provision database roles', ['psql', '-h', '/var/run/postgresql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], user='postgres', data=sql)


def worker_fresh():
    try:
        age = time.time() - float((RUN / 'live/classification-ready').read_text())
        return 0 <= age < 60
    except (OSError, ValueError):
        return False


def launch(supervisor):
    os.umask(0o077)
    STATE.mkdir(parents=True, exist_ok=True)
    lock = (STATE / '.pod.lock').open('a')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise RuntimeError('Persistent state is already locked by another Pod; only one writer may run') from None
    RUN.mkdir(mode=0o755, exist_ok=True)
    RUN.chmod(0o755)
    shutil.chown(RUN, user='root', group='root')
    (RUN / 'live').mkdir(mode=0o700, exist_ok=True)
    shutil.chown(RUN / 'live', user='swiftaid', group='swiftaid')
    for name in ('security', 'keys', 'postgres', 'redis', 'jetstream', 'qdrant', 'objects', 'models'):
        path = STATE / name
        path.mkdir(mode=0o700, exist_ok=True)
        path.chmod(0o750 if name == 'security' else 0o700)
        owner = 'postgres' if name == 'postgres' else 'root' if name == 'security' else 'swiftaid'
        shutil.chown(path, user=owner, group='postgres' if name == 'postgres' else 'swiftaid')
    STATE.chmod(0o755)
    values = load_secrets(STATE / 'security' / 'credentials.json')
    env = gateway_environment(STATE, values, dict(os.environ))
    certificates(supervisor, values)
    write_configs(values)
    base = dict(os.environ, SSL_CERT_FILE='/etc/ssl/certs/ca-certificates.crt',
                REQUESTS_CA_BUNDLE='/etc/ssl/certs/ca-certificates.crt', HOME='/run/swiftaid/live',
                HF_HOME=str(STATE / 'models' / 'hf'), HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1')
    env.update({key: base[key] for key in ('SSL_CERT_FILE', 'REQUESTS_CA_BUNDLE')})
    database(supervisor, base, values)
    supervisor.start('redis', ['redis-server', str(RUN / 'redis.conf')], base)
    nats_env = dict(base)
    for role in ('gateway', 'bootstrap', 'worker'):
        nats_env[f'NATS_{role.upper()}_USER'] = role
        nats_env[f'NATS_{role.upper()}_PASSWORD'] = values['nats_' + role]
    supervisor.start('nats', ['nats-server', '-c', str(RUN / 'nats.conf')], nats_env)
    supervisor.start('qdrant', ['qdrant', '--config-path', str(RUN / 'qdrant.yaml')], base, cwd=str(STATE / 'qdrant'))
    minio_env = dict(base, MINIO_ROOT_USER='swiftaid-root', MINIO_ROOT_PASSWORD=values['minio_root'])
    supervisor.start('minio', ['minio', 'server', str(STATE / 'objects'), '--address', '127.0.0.1:9000',
                     '--console-address', '127.0.0.1:9001', '--certs-dir', str(RUN / 'minio-tls')], minio_env)
    redis_env = dict(base, REDISCLI_AUTH=values['redis'])
    probes = {
        'postgres': lambda: command_ok(['psql', '-h', '/var/run/postgresql', '-U', 'postgres', '-Atc', 'SELECT 1'], user='postgres'),
        'redis': lambda: command_ok(['redis-cli', '--tls', '--cacert', '/etc/ssl/certs/ca-certificates.crt',
                                    '-h', 'localhost', 'ping'], redis_env),
        'nats': lambda: http_ok('http://127.0.0.1:8222/healthz?js-enabled-only=true'),
        'qdrant': lambda: http_ok('https://localhost:6333/readyz', {'api-key': values['qdrant']}),
        'minio': lambda: http_ok('https://localhost:9000/minio/health/ready'),
    }
    for name, probe in probes.items():
        supervisor.wait(name, probe)
    # mc uses a private config file so root credentials never appear in its command line.
    private_file(RUN / 'config.json', json.dumps({'version': '10', 'aliases': {'pod': {
        'url': 'https://localhost:9000', 'accessKey': 'swiftaid-root', 'secretKey': values['minio_root'],
        'api': 'S3v4', 'path': 'auto'}}}))
    mc = ['mc', '--config-dir', str(RUN)]
    supervisor.run('Create private media bucket', mc + ['mb', '--ignore-existing', 'pod/swiftaid-private-media'], base)
    supervisor.run('Provision media identity', mc + ['admin', 'user', 'add', 'pod', 'swiftaid-app', values['minio_app']], base)
    policy = {'Version': '2012-10-17', 'Statement': [
        {'Effect': 'Allow', 'Action': ['s3:GetBucketLocation', 's3:ListBucket'], 'Resource': ['arn:aws:s3:::swiftaid-private-media']},
        {'Effect': 'Allow', 'Action': ['s3:GetObject', 's3:PutObject', 's3:DeleteObject', 's3:AbortMultipartUpload',
                                      's3:ListMultipartUploadParts'], 'Resource': ['arn:aws:s3:::swiftaid-private-media/*']}]}
    private_file(RUN / 'media-policy.json', json.dumps(policy))
    supervisor.run('Create media policy', mc + ['admin', 'policy', 'create', 'pod', 'swiftaid-media', str(RUN / 'media-policy.json')], base)
    supervisor.run('Attach media policy', mc + ['admin', 'policy', 'attach', 'pod', 'swiftaid-media', '--user', 'swiftaid-app'], base)
    bootstrap = dict(base, NATS_URL='tls://localhost:4222', NATS_USER='bootstrap', NATS_PASSWORD=values['nats_bootstrap'],
                     APPSETTINGS_PATH='/opt/gateway/appsettings.json')
    for script in ('bootstrap-notification-stream.sh', 'bootstrap-ingestion-stream.sh'):
        supervisor.run(script, ['sh', str(ROOT / script)], bootstrap)
    python_worker = '/opt/workers/.venv/bin/python'
    supervisor.run('Provision auxiliary streams', [python_worker, str(ROOT / 'bootstrap_streams.py')], bootstrap)
    supervisor.run('Provision classification consumer', [python_worker, '-m', 'swiftaid_workers.classification.provision'], bootstrap)
    migration = dict(env, ConnectionStrings__SwiftAid=connection_string(values['database_migrate'], 'swiftaid_migrate'))
    print('Applying database migrations before Gateway startup', flush=True)
    supervisor.run('Database migration', ['dotnet', '/opt/gateway/SwiftAid.Gateway.dll', '--migrate-database'],
                   migration, 'swiftaid', timeout=900, show_output=True)
    supervisor.run('Grant runtime data access', ['psql', '-h', '/var/run/postgresql', '-U', 'postgres', '-d', 'swiftaid', '-v', 'ON_ERROR_STOP=1'],
                   user='postgres', data='GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO swiftaid_app;\nGRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO swiftaid_app;\n')
    # This may take a long time on a cold volume. Model progress is visible, credentials are not.
    download = dict(base, HF_HUB_OFFLINE='0', TRANSFORMERS_OFFLINE='0')
    command = ['/opt/tts/.venv/bin/python', str(ROOT / 'provision_models.py')]
    if os.environ.get('RUNPOD_DOWNLOAD_MODELS', 'true').lower() != 'true':
        command.append('--verify-only')
    supervisor.start('model-provision', command, download)
    _, downloader = supervisor.children[-1]
    deadline = time.monotonic() + 7200
    while downloader.poll() is None:
        supervisor.check(expected_exit=downloader)
        if time.monotonic() > deadline:
            raise RuntimeError('Model provisioning exceeded two hours')
        time.sleep(1)
    supervisor.children.pop()
    if downloader.returncode:
        raise RuntimeError('Model provisioning failed; no application services started')
    extra = json.loads((ROOT / 'models.lock.json').read_text())
    folders = {model['model']: str(STATE / 'models' / model['model'].split('/')[1] / model['revision']) for model in extra['models']}
    speech = dict(base, ELLAI_TTS_MODEL_PATH=folders['facebook/mms-tts-eng'], ELLAI_TTS_ENGLISH_PROVIDER='kokoro',
                  ELLAI_KOKORO_MODEL_PATH=folders['hexgrad/Kokoro-82M'], ELLAI_TTS_INTERNAL_KEY=values['tts'],
                  ELLAI_TTS_NATIVE_PROFILES=native_profiles(STATE, json.loads(Path('/opt/tts/native-models.lock.json').read_text())),
                  PYTHONPATH=str(STATE / 'models' / 'spacy-site'))
    supervisor.start('tts', ['/opt/tts/.venv/bin/python', '-m', 'uvicorn', 'app:app', '--host', '127.0.0.1', '--port', '8090'], speech, cwd='/opt/tts')
    language = dict(base, ELLAI_LANGUAGE_MODEL_PATH=str(STATE / 'models'), ELLAI_LANGUAGE_INTERNAL_KEY=values['language'])
    supervisor.start('language', ['/opt/tts/.venv/bin/python', '-m', 'uvicorn', 'language_app:app', '--host', '127.0.0.1', '--port', '8091'], language, cwd='/opt/tts')
    for name, port in [('tts', 8090), ('language', 8091)]:
        probes[name] = lambda port=port: http_ok(f'http://127.0.0.1:{port}/health/ready')
        supervisor.wait(name, probes[name], timeout=600)
    supervisor.start('gateway', ['dotnet', '/opt/gateway/SwiftAid.Gateway.dll'], env, cwd='/opt/gateway')
    probes['gateway'] = lambda: http_ok('https://localhost:8443/health/live')
    supervisor.wait('gateway', probes['gateway'], timeout=300)
    worker = dict(base, CLASSIFICATION_NATS_URL='tls://localhost:4222', CLASSIFICATION_NATS_USER='worker',
                  CLASSIFICATION_NATS_PASSWORD=values['nats_worker'], CLASSIFICATION_GATEWAY_URL='https://localhost:8443',
                  CLASSIFICATION_WORKER_TOKEN=values['classification'], CLASSIFICATION_DEVICE='cpu',
                  CLASSIFICATION_HEALTH_FILE=str(RUN / 'live/classification-ready'))
    (RUN / 'live/classification-ready').unlink(missing_ok=True)
    supervisor.start('classification', [python_worker, '-m', 'swiftaid_workers', '--classification'], worker)
    probes['classification'] = worker_fresh
    supervisor.wait('classification', worker_fresh, timeout=600)
    supervisor.start('readiness', ['python', str(ROOT / 'readiness.py')], base)
    supervisor.start('nginx', ['nginx', '-c', str(RUN / 'nginx.conf'), '-g', 'daemon off;'], base)
    print('SwiftAid startup complete; expose HTTP port 8080 through the Runpod HTTPS proxy', flush=True)
    failures = 0
    while not supervisor.stopping:
        supervisor.check()
        status = {name: probe() for name, probe in probes.items()}
        ready = all(status.values())
        private_file(RUN / 'health.json', json.dumps(dict(checkedAt=time.time(), ready=ready, services=status)))
        failures = 0 if ready else failures + 1
        if failures >= 3:
            failed = ', '.join(name for name, available in status.items() if not available)
            raise RuntimeError(f'Dependency readiness failed three consecutive times: {failed}; stopping stack')
        for _ in range(10):
            if supervisor.stopping:
                break
            time.sleep(1)
    lock.close()


def main():
    supervisor = Supervisor()
    signal.signal(signal.SIGTERM, supervisor.signal)
    signal.signal(signal.SIGINT, supervisor.signal)
    try:
        launch(supervisor)
    except InterruptedError:
        pass
    except Exception as error:
        # Only controlled messages: third-party exceptions can contain credentials or connection strings.
        if isinstance(error, (RuntimeError, ValueError)):
            print(f'Startup stopped: {error}', flush=True)
        else:
            print(f'Startup stopped: {type(error).__name__}; inspect preceding service logs', flush=True)
        return 1
    finally:
        supervisor.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
