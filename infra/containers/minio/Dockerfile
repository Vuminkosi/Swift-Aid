# Source-only MinIO release image for opt-in local SwiftAid development.
FROM --platform=linux/amd64 golang:1.24.8-alpine@sha256:e89daba6691cee7e338c17467e3d8777ff18d80ee62ff544808971fd51189282 AS build

ARG GOMAXPROCS=2
ARG MINIO_RELEASE=RELEASE.2025-10-15T17-29-55Z
ARG MINIO_COMMIT=9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a
ARG MINIO_SHORT_COMMIT=9e49d5e7a648
ARG MINIO_SOURCE_SHA256=BE6D0BD3696C3A13A35F02D3A0280B64319C67918B4501C5C3D87F96D000085C

ENV CGO_ENABLED=0 \
    GOMAXPROCS=${GOMAXPROCS} \
    GOOS=linux \
    GOARCH=amd64

RUN apk add --no-cache ca-certificates

WORKDIR /src
RUN wget -qO /tmp/minio-source.tar.gz "https://github.com/minio/minio/archive/refs/tags/${MINIO_RELEASE}.tar.gz" \
    && echo "${MINIO_SOURCE_SHA256}  /tmp/minio-source.tar.gz" | sha256sum -c - \
    && mkdir minio \
    && tar -xzf /tmp/minio-source.tar.gz --strip-components=1 -C minio \
    && mkdir -p /out

WORKDIR /src/minio
RUN go build -tags kqueue -trimpath \
    -ldflags="-s -w -X github.com/minio/minio/cmd.Version=${MINIO_RELEASE} -X github.com/minio/minio/cmd.CopyrightYear=2025 -X github.com/minio/minio/cmd.ReleaseTag=${MINIO_RELEASE} -X github.com/minio/minio/cmd.CommitID=${MINIO_COMMIT} -X github.com/minio/minio/cmd.ShortCommitID=${MINIO_SHORT_COMMIT}" \
    -o /out/minio .

FROM --platform=linux/amd64 alpine:3.22.2@sha256:85f2b723e106c34644cd5851d7e81ee87da98ac54672b29947c052a45d31dc2f

ARG MINIO_RELEASE=RELEASE.2025-10-15T17-29-55Z
ARG MINIO_COMMIT=9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a
LABEL org.opencontainers.image.source="https://github.com/minio/minio" \
      org.opencontainers.image.version="${MINIO_RELEASE}" \
      org.opencontainers.image.revision="${MINIO_COMMIT}"

COPY --from=build /out/minio /usr/bin/minio
COPY --from=build /src/minio/dockerscripts/docker-entrypoint.sh /usr/bin/docker-entrypoint.sh
COPY --from=build /src/minio/LICENSE /licenses/LICENSE
COPY --from=build /src/minio/CREDITS /licenses/CREDITS
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt

EXPOSE 9000
VOLUME ["/data"]
ENTRYPOINT ["/usr/bin/docker-entrypoint.sh"]
CMD ["minio"]
