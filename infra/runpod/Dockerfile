# syntax=docker/dockerfile:1
FROM ghcr.io/astral-sh/uv:0.13.5 AS uv
FROM python:3.12.15-slim-bookworm AS python
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS dotnet
FROM nats:2.15.1-alpine3.22 AS nats
FROM natsio/nats-box:0.19.7@sha256:ffce8bd103383f179f8c7f11cf645726acf5d17280706c530c3b342dbe16334c AS nats-cli
FROM redis:8.4.7@sha256:f07fb2e3e4ca6e12113a77e263a43d3e3d5069135de2d9f0f550538ee116c3e8 AS redis
FROM qdrant/qdrant:v1.19.0@sha256:6c0652f8d6925b22f2f6f0e0a5365a6c9dbc8768bd6e70ccc1cdc14847e452a0 AS qdrant

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS gateway-build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props SwiftAid.sln ./
COPY src ./src
COPY resources ./resources
RUN dotnet restore src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --locked-mode \
    && dotnet publish src/SwiftAid.Gateway/SwiftAid.Gateway.csproj -c Release --no-restore \
       -o /publish -p:UseAppHost=false -m:1 -nr:false

# Match the source releases and archive digests used by infra/containers.
FROM golang:1.24.8-alpine@sha256:e89daba6691cee7e338c17467e3d8777ff18d80ee62ff544808971fd51189282 AS object-build
ENV CGO_ENABLED=0 GOMAXPROCS=2 GOOS=linux GOARCH=amd64
RUN apk add --no-cache ca-certificates
WORKDIR /source
RUN wget -qO minio.tar.gz https://github.com/minio/minio/archive/refs/tags/RELEASE.2025-10-15T17-29-55Z.tar.gz \
    && echo 'be6d0bd3696c3a13a35f02d3a0280b64319c67918b4501c5c3d87f96d000085c  minio.tar.gz' | sha256sum -c - \
    && mkdir minio /out && tar -xzf minio.tar.gz --strip-components=1 -C minio \
    && cd minio && go build -tags kqueue -trimpath -ldflags='-s -w -X github.com/minio/minio/cmd.Version=RELEASE.2025-10-15T17-29-55Z' -o /out/minio .
RUN wget -qO mc.tar.gz https://github.com/minio/mc/archive/refs/tags/RELEASE.2025-08-13T08-35-41Z.tar.gz \
    && echo '29db22500374169a43951c7cef09daf19e7291ea5ba00ac10f321371b0a35b32  mc.tar.gz' | sha256sum -c - \
    && mkdir mc && tar -xzf mc.tar.gz --strip-components=1 -C mc \
    && cd mc && go build -trimpath -ldflags='-s -w -X github.com/minio/mc/cmd.Version=RELEASE.2025-08-13T08-35-41Z' -o /out/mc .

# Separate locked environments: the classification and speech Torch versions differ.
FROM python AS python-build
COPY --from=uv /uv /uvx /usr/local/bin/
ENV UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never
WORKDIR /opt/tts
COPY infra/ellai-tts/pyproject.toml infra/ellai-tts/uv.lock ./
RUN uv sync --frozen --no-dev --no-cache --extra kokoro
COPY infra/ellai-tts/*.py ./
COPY scripts/ellai/provision_native_models.py scripts/ellai/native-models.lock.json ./
WORKDIR /opt/workers
COPY workers/pyproject.toml workers/uv.lock ./
COPY workers/src ./src
RUN uv sync --frozen --no-dev --no-cache --extra classification

FROM postgis/postgis:17-3.6@sha256:5d4c5c5d10b415bec52b0b9cea1ae93ace3a451ff10ab8f8974988e336274452 AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
      nginx openssl ca-certificates curl jq libicu76 libgomp1 libatomic1 libespeak-ng1 libjemalloc2 \
      libbz2-1.0 libexpat1 libffi8 liblzma5 libsqlite3-0 libreadline8t64 libgdbm6t64 libgdbm-compat4t64 zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 swiftaid && useradd --uid 10001 --gid swiftaid --no-create-home swiftaid
COPY --from=python /usr/local/ /usr/local/
RUN ldconfig
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
COPY --from=nats /usr/local/bin/nats-server /usr/local/bin/nats-server
COPY --from=nats-cli /usr/local/bin/nats /usr/local/bin/nats
COPY --from=redis /usr/local/bin/redis-server /usr/local/bin/redis-cli /usr/local/bin/
COPY --from=qdrant /qdrant/qdrant /usr/local/bin/qdrant
COPY --from=object-build /out/ /usr/local/bin/
COPY --from=object-build /source/minio/LICENSE /opt/licenses/minio-LICENSE
COPY --from=object-build /source/mc/LICENSE /opt/licenses/mc-LICENSE
COPY --from=python-build /opt/tts /opt/tts
COPY --from=python-build /opt/workers /opt/workers
COPY --from=gateway-build /publish /opt/gateway
COPY docs/ellai/ellai-system-instruction.md /etc/swiftaid/policy/ellai-system-instruction.md
COPY docs/TECHNICAL.md /etc/swiftaid/docs/TECHNICAL.md
COPY infra/nats.conf /opt/runpod/nats-base.conf
COPY infra/scripts/bootstrap-notification-stream.sh infra/scripts/bootstrap-ingestion-stream.sh /opt/runpod/
COPY infra/runpod/ /opt/runpod/
ENV PATH="/usr/share/dotnet:/usr/lib/postgresql/17/bin:${PATH}" \
    DOTNET_ROOT=/usr/share/dotnet PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 \
    SWIFTAID_STATE=/workspace/swiftaid HF_HUB_DISABLE_TELEMETRY=1 \
    HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1
WORKDIR /opt/gateway
EXPOSE 8080
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=10s --start-period=30m --retries=3 \
    CMD ["python", "/opt/runpod/health.py"]
ENTRYPOINT ["python", "/opt/runpod/start.py"]
CMD []
