# Validation record — 2026-11-05

Scope: one-container Runpod deployment configuration on `interactive-ellai`, based on application
revision `a9435520`. No application source changes, production restart, registry push or Pod deployment.

| Status | Check | Observed result / limit |
|---|---|---|
| PASS | `docker compose -f infra/compose-runpod.yml config --quiet` | Final Compose configuration parses; one application container. |
| PASS | `docker run --rm --mount type=bind,source=C:/DevOps/SwiftAidv2,target=/repo,readonly --workdir /repo python:3.12.15-slim-bookworm python -m unittest discover -s infra/runpod/tests -v` | 17 final-source tests pass on Linux: configuration, model integrity, archive boundaries, credential persistence, readiness and process failure handling. |
| PASS | Disposable Python container running `tests/smoke_certificates.py` | Real OpenSSL trust and hostname checks; PKCS12 files readable by the application identity; identity/keyring certificates unchanged across restart. Completed before the instruction to stop local builds. |
| PASS | `docker build -f infra/runpod/tests/Dockerfile.postgres -t swiftaid/runpod-pg-smoke:task infra/runpod`, followed by `docker run --rm --network none swiftaid/runpod-pg-smoke:task` | Isolated PostgreSQL/PostGIS initialization, verified TLS, migration/runtime role separation, denied runtime schema creation, and retained data after restart. Completed before the instruction to stop local builds; disposable tagged test image removed. |
| PASS | Gateway stage of `python infra/runpod/build.py` | Locked restore and Release publish reached `/publish`. Existing WebAssembly native-reference linking warning observed; not a zero-warning build. |
| FAIL / workaround checked | Standard Docker build-context collection on this Windows checkout | Docker attempted to traverse protected `.tmp-smoke/dp-keys` despite exclusions. The bounded-context `build.py` helper passed context collection and reached the dependency builds. |
| NOT VERIFIED | Complete final image build | Local image build was stopped at the user's request. Gateway publishing completed, but the complete image and final runtime packaging were not accepted. Build on the designated remote builder. |
| NOT VERIFIED | Runpod cold startup and full-stack restart | Requires the completed image, mounted storage, real Muse configuration and explicit authorized organization UUIDs. |
| NOT VERIFIED | Downloading the complete model inventory at Pod startup; voice and case workflows | Manifest integrity and failure handling tested; the full 11.11 GiB download and live inference/workflows were not exercised in the new container. |
| NOT APPLICABLE | CUDA inference / local Llama | This configuration retains hosted Muse and the existing CPU speech/language/classification implementations. |

Local build/smoke logs remain under ignored `artifacts/runpod/`. Shared Docker caches, databases,
volumes and the existing Gateway were preserved. Do not interpret unit tests or the database-only
smoke test as full deployment acceptance.

Automatic approval review rejected removal of the task-created temporary test context and Python
bytecode directories as "blocked by policy". Those ignored artifacts were retained, outside the commit.
