# SwiftAid in one Runpod Pod container

`../compose-runpod.yml` builds and runs **one container**, with no Docker socket or nested daemon.
Runpod Pods consume the built image and Pod settings, not a Compose file. The existing multi-container
`infra/compose.yml` is unchanged.

## Included processes

| Process | Private address | Persistent state |
|---|---|---|
| nginx public ingress | `0.0.0.0:8080` | none |
| ASP.NET Core Gateway and its registered hosted workers | `https://localhost:8443` | PostgreSQL and keyring |
| PostgreSQL 17 / PostGIS | TLS `localhost:5432` | `postgres/` |
| Redis | TLS `localhost:6379` | `redis/` |
| NATS JetStream | TLS `localhost:4222` | `jetstream/` |
| Qdrant | HTTPS `localhost:6333` | `qdrant/` |
| MinIO | HTTPS `localhost:9000` | `objects/` |
| Classification consumer | internal NATS + authenticated Gateway API | durable broker / database |
| Ellai speech worker | `http://127.0.0.1:8090` | model cache |
| Ellai language worker | `http://127.0.0.1:8091` | model cache |

All backing services bind loopback. The Python classification consumer is the implemented worker entry
point; application orchestration, ingestion dispatch, follow-ups and other registered .NET background
services run inside Gateway. This deployment does not invent standalone worker implementations.
Identity is enabled with persistent signing/encryption certificates and the existing public client
registration. External SMTP, federation and push delivery still require their existing operator-supplied
configuration if enabled. MAUI remains a separately distributed client of the public API.

## Build and configure

Run builds on your designated remote builder, **not this local workstation**. From a checkout of the
repository there (Docker Compose supporting optional `env_file` is required):

```powershell
Copy-Item infra/runpod/runtime.env.example infra/runpod/runtime.env
# Edit runtime.env: real provider values and explicit authorized organization UUIDs.
docker compose -f infra/compose-runpod.yml config --quiet
docker compose -f infra/compose-runpod.yml build
docker compose -f infra/compose-runpod.yml up -d
docker compose -f infra/compose-runpod.yml logs -f --tail 100
```

The local public port is `127.0.0.1:8088`. HTTP localhost is only a connectivity check: use HTTPS for
browser cookies and microphone acceptance. Production transport settings are intentionally preserved.
`runtime.env` is ignored by Git and excluded from the Docker build context. Do not put secrets in build
arguments, image layers or committed Compose files. Existing `.env` files are not automatically imported.

If a Windows Docker client traverses an inaccessible ignored test directory while collecting context,
use `python infra/runpod/build.py` (Python 3.12+) to build from an explicitly selected temporary context,
then `docker compose -f infra/compose-runpod.yml up -d --no-build`. This helper includes source and locked
dependencies, excludes private files and caches, and removes only its own temporary directory afterward.

`RUNPOD_IMAGE` changes the output image tag. Build and registry publication are separate operator
actions; no push is performed by this configuration. Hosted Muse is retained for reasoning and voice
transcription. There is no bundled Ollama runtime.

## Runpod settings

1. Supply the built image in a Pod template. Use its entrypoint; do not replace it with a sleep command.
2. Attach persistent storage at **`/workspace`**. All authoritative state is under `/workspace/swiftaid`.
   Use one writer Pod per state volume; the supervisor takes an exclusive file lock.
3. Expose **HTTP port 8080** through the Runpod HTTPS proxy. No public TCP database, cache, broker,
   vector store, object store, speech or language ports are needed.
4. Set the values in `runtime.env.example` as Pod environment variables. Omit `SWIFTAID_PUBLIC_HOST`
   to derive the standard hostname from `RUNPOD_POD_ID`, or set the public hostname explicitly.
5. Allow outbound HTTPS for pinned model downloads and the configured reasoning / transcription provider.
6. Start with at least 16 CPU threads, 32 GiB RAM, 60 GiB container disk and 100 GiB persistent storage.
   These are sizing estimates, not a measured capacity guarantee; monitor workload and media growth.

Current Python model environments use their locked **CPU Torch** builds. Selecting a GPU Pod does not
automatically move speech, translation or classification onto CUDA. This configuration uses the existing
CPU inference implementations and does not claim GPU acceleration.

See Runpod's [port exposure](https://docs.runpod.io/pods/configuration/expose-ports) and
[storage lifetime documentation](https://docs.runpod.io/pods/storage/types). Container-disk state is
not a substitute for the mounted persistent volume. Keep backups outside the Pod.

## Model provisioning and startup

Startup downloads and verifies pinned artifacts for:

- BART Large MNLI classification;
- NLLB-200 distilled 1.3B translation and MMS-1B speech recognition;
- MMS English, Shona, Nyanja and Tsonga speech synthesis;
- Kokoro-82M English Heart/Michael voices and the pinned English linguistic model.

The native inventory is reused from `scripts/ellai/native-models.lock.json`; additional artifacts are in
`models.lock.json`. LFS files use SHA-256; ordinary Hugging Face files use Git blob hashes plus exact size.
Artifacts stay in the persistent `models/` directory and are verified on later starts. Inference workers
run in offline mode. The pinned inventory totals approximately 11.11 GiB before cache metadata.
Set `RUNPOD_DOWNLOAD_MODELS=false` for an already provisioned offline cache; a missing
or corrupt model then stops startup. Do not configure an untrusted model repository or arbitrary revision.

Muse reasoning and the existing live voice transcription transport are **hosted APIs**, not downloadable
checkpoints. They require the configured provider credentials. Downloading models does not validate language
quality or grant model licenses; retain the upstream licensing obligations for the chosen deployment.

Startup initializes backing services, idempotently verifies broker streams/consumers, creates the private
media bucket and scoped identity, runs database migrations using a separate database owner, downloads
models, then starts speech/language, Gateway and classification. The runtime database account has data
access, not migration ownership. An incompatible retained stream or failed migration stops startup.

Internal passwords, identity certificates and the private TLS CA are generated once in `security/`. Production TLS checks remain
enabled. Data Protection uses a separate persistent certificate in addition to `keys/`; back them up
together with the database. Never delete credentials or certificates to repair a startup failure.

## Health, shutdown, upgrades and recovery

`/pod-health/ready` returns 200 only while all dependency probes and the classification health lease are
fresh. `/health/live` alone is not stack readiness. Docker health uses the same aggregate status; Runpod
HTTP checks should use `/pod-health/ready`. A child exit or three consecutive failed dependency checks
stops the entire stack. Docker Compose restarts it according to its policy; configure Pod monitoring and
restart policy separately in Runpod.

SIGTERM stops children in reverse order. Allow at least 90 seconds before forcibly killing the Pod.
Cold downloads may take many minutes; the healthcheck allows a 30-minute startup period, while the model
download supervisor has a two-hour limit. Investigate repeated failures rather than deleting volumes.

Before changing image or model revisions, stop intake, take a consistent PostgreSQL dump and backup the
private media, JetStream state, `security/` and `keys/` using an appropriate encrypted backup procedure.
Preserve the previous image and matching backup. Migrations can make rollback to an older image unsafe;
restore a compatible backup or fix forward. This is a single failure domain, not high availability.

Validation commands:

```powershell
python -m unittest discover -s infra/runpod/tests -v
docker compose -f infra/compose-runpod.yml config --quiet
docker compose -f infra/compose-runpod.yml build
docker compose -f infra/compose-runpod.yml exec swiftaid python /opt/runpod/health.py
```

The process tests require Linux (Windows skips those five cases). They can run in the Python base image
with the repository mounted read-only. `tests/smoke_certificates.py` runs in a disposable root Python image
and checks actual OpenSSL trust and certificate reuse. For an isolated PostgreSQL/PostGIS test:

```powershell
docker build -f infra/runpod/tests/Dockerfile.postgres -t swiftaid/runpod-pg-smoke:test infra/runpod
docker run --rm --network none swiftaid/runpod-pg-smoke:test
```

That test initializes only its temporary database, verifies TLS and application-role restrictions, and
checks persistence across process restart. It does not connect to the running SwiftAid database.

Real Pod startup, full-stack persistence across restart, voice provider calls and report submission
must be assessed in the target environment; config validation alone is not end-to-end acceptance.
