# Minimal official MinIO Client source build for the opt-in local bootstrap profile.
FROM --platform=linux/amd64 golang:1.24.8-alpine@sha256:e89daba6691cee7e338c17467e3d8777ff18d80ee62ff544808971fd51189282 AS build

ARG GOMAXPROCS=2
ARG MC_RELEASE=RELEASE.2025-08-13T08-35-41Z
ARG MC_COMMIT=d6541ea280b73a834b64d4097e21f2be77676104
ARG MC_SHORT_COMMIT=d6541ea280b7
ARG MC_SOURCE_SHA256=29DB22500374169A43951C7CEF09DAF19E7291EA5BA00AC10F321371B0A35B32

ENV CGO_ENABLED=0 \
    GOMAXPROCS=${GOMAXPROCS} \
    GOOS=linux \
    GOARCH=amd64

RUN apk add --no-cache ca-certificates

WORKDIR /src
RUN wget -qO /tmp/mc-source.tar.gz "https://github.com/minio/mc/archive/refs/tags/${MC_RELEASE}.tar.gz" \
    && echo "${MC_SOURCE_SHA256}  /tmp/mc-source.tar.gz" | sha256sum -c - \
    && mkdir mc \
    && tar -xzf /tmp/mc-source.tar.gz --strip-components=1 -C mc \
    && mkdir -p /out

WORKDIR /src/mc
RUN go build -trimpath \
    -ldflags="-s -w -X github.com/minio/mc/cmd.Version=${MC_RELEASE} -X github.com/minio/mc/cmd.CopyrightYear=2025 -X github.com/minio/mc/cmd.ReleaseTag=${MC_RELEASE} -X github.com/minio/mc/cmd.CommitID=${MC_COMMIT} -X github.com/minio/mc/cmd.ShortCommitID=${MC_SHORT_COMMIT}" \
    -o /out/mc .

FROM --platform=linux/amd64 alpine:3.22.2@sha256:85f2b723e106c34644cd5851d7e81ee87da98ac54672b29947c052a45d31dc2f

ARG MC_RELEASE=RELEASE.2025-08-13T08-35-41Z
ARG MC_COMMIT=d6541ea280b73a834b64d4097e21f2be77676104
LABEL org.opencontainers.image.source="https://github.com/minio/mc" \
      org.opencontainers.image.version="${MC_RELEASE}" \
      org.opencontainers.image.revision="${MC_COMMIT}"

COPY --from=build /out/mc /usr/bin/mc
COPY --from=build /src/mc/LICENSE /licenses/LICENSE
COPY --from=build /src/mc/CREDITS /licenses/CREDITS
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt

ENTRYPOINT ["mc"]
