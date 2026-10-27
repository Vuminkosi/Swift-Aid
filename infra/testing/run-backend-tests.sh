#!/bin/sh
set -eu

fail() {
    printf '%s\n' "SwiftAid Docker backend tests: $1" >&2
    exit 1
}

wait_for_service() {
    service_name=$1
    host=$2
    port=$3

    attempt=0
    while [ "$attempt" -lt 60 ]; do
        if socat -T 1 -u /dev/null "TCP:$host:$port,connect-timeout=1" >/dev/null 2>&1; then
            printf 'Fixture ready: %s (%s:%s)\n' "$service_name" "$host" "$port"
            return 0
        fi

        attempt=$((attempt + 1))
        sleep 1
    done

    fail "required fixture $service_name did not become reachable at $host:$port."
}

for required_path in \
    /source/global.json \
    /source/Directory.Build.props \
    /source/Directory.Packages.props \
    /source/src \
    /source/tests \
    /package-assets/src \
    /package-assets/tests \
    /test-output; do
    [ -e "$required_path" ] || fail "required read-only source or output mount is missing: $required_path."
done

[ "${SWIFTAID_RUN_SQLAUTH_FIXTURES:-}" = 1 ] ||
    fail "the PostgreSQL SQLAUTH fixture flag is required."
: "${SWIFTAID_TEST_POSTGRES_CONNECTION:?The isolated SQLAUTH PostgreSQL connection is required.}"
: "${SWIFTAID_TEST_IDENTITY_POSTGRES_CONNECTION:?The isolated identity PostgreSQL connection is required.}"
: "${SWIFTAID_TEST_REDIS_CONNECTION:?The isolated Redis connection is required.}"
[ "${SWIFTAID_RUN_NATS_FIXTURES:-}" = 1 ] ||
    fail "the NATS fixture flag is required."
: "${SWIFTAID_TEST_NATS_URL:?The isolated NATS URL is required.}"

for project in \
    /source/tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj \
    /source/tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj \
    /source/tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj; do
    [ -r "$project" ] || fail "required backend test project is missing: $project."
    [ -r "${project%/*}/packages.lock.json" ] || fail "locked NuGet packages are required for ${project%/*}."
done

wait_for_service postgresql postgres 5432
wait_for_service redis redis 6379
wait_for_service nats nats 4222

socat TCP-LISTEN:32703,bind=127.0.0.1,reuseaddr,fork TCP:postgres:5432,connect-timeout=2 \
    >/dev/null 2>&1 &
postgres_proxy_pid=$!
trap 'kill "$postgres_proxy_pid" 2>/dev/null || true' EXIT HUP INT TERM
wait_for_service sqlauth-postgres-loopback 127.0.0.1 32703
wait_for_service identity-postgres swiftaid-identity-postgres 5432

mkdir -p /workspace/src /workspace/tests \
    /test-output/domain \
    /test-output/application \
    /test-output/integration \
    /test-output/identity

cp /source/global.json /workspace/global.json
cp /source/Directory.Build.props /workspace/Directory.Build.props
cp /source/Directory.Packages.props /workspace/Directory.Packages.props
cp -a /source/src/. /workspace/src/
cp -a /source/tests/. /workspace/tests/

# Ignore any pre-existing build output in the host checkout. Tests compile from
# the read-only source snapshot into the runner's private work copy.
find /workspace/src /workspace/tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
cp -a /package-assets/src/. /workspace/src/
cp -a /package-assets/tests/. /workspace/tests/

cd /workspace

dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj \
    --configuration Release \
    --no-restore \
    --results-directory /test-output/domain \
    --logger "trx;LogFileName=SwiftAid.Domain.Tests.trx" \
    -p:BaseOutputPath=/test-output/bin/

dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj \
    --configuration Release \
    --no-restore \
    --results-directory /test-output/application \
    --logger "trx;LogFileName=SwiftAid.Application.Tests.trx" \
    -p:BaseOutputPath=/test-output/bin/

dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj \
    --configuration Release \
    --no-restore \
    --results-directory /test-output/integration \
    --logger "trx;LogFileName=SwiftAid.Integration.Tests.trx" \
    -p:BaseOutputPath=/test-output/bin/

env -u SWIFTAID_RUN_SQLAUTH_FIXTURES \
    -u SWIFTAID_TEST_POSTGRES_CONNECTION \
    SWIFTAID_RUN_IDENTITY_POSTGRES_FIXTURES=1 \
    dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj \
        --configuration Release \
        --no-restore \
        --filter 'FullyQualifiedName~ExternalIdentityMappingPolicyTests|FullyQualifiedName~IdentityEndpointSecurityTests' \
        --results-directory /test-output/identity \
        --logger "trx;LogFileName=SwiftAid.Identity.Postgres.Tests.trx" \
        -p:BaseOutputPath=/test-output/bin/

printf '%s\n' \
    'SQLAUTH and identity PostgreSQL fixtures ran in separate passes because the existing test guards intentionally reject simultaneous SQLAUTH and identity fixture flags.' \
    'INCOMPLETE backend suite: eight opt-in provider tests were skipped because their current fixtures self-start Docker containers with host-published ports or require a separately provisioned Facebook TLS test certificate.' \
    'Skipped: MinioPrivateMediaObjectStoreTests.OpenReadAsyncReturnsOnlyTheRequestedRangeAndMetadata' \
    'Skipped: MinioPrivateMediaObjectStoreTests.OpenReadAsyncRejectsRangesOutsideTheAuthoritativeObjectLength' \
    'Skipped: MinioPrivateMediaObjectStoreTests.OpenReadAsyncReturnsNullForMissingObjects' \
    'Skipped: MinioPrivateMediaObjectStoreTests.OpenReadAsyncHonorsCancellationBeforeProviderAccess' \
    'Skipped: ProviderAdapterContainerTests.MinioAdapterStoresAndCopiesPrivateOpaqueObjectsAndEnforcesSizeLimit' \
    'Skipped: ProviderAdapterContainerTests.RedisAdapterReadsWritesExpiresAndRemovesDerivedCacheEntries' \
    'Skipped: ProviderAdapterContainerTests.JetStreamAdaptersPublishOnlyToConfiguredStreamsAndReportStorageAcknowledgement' \
    'Skipped: FacebookWebLoginExchangeTests.ProductionValidator_UsesFixedTlsHostAndDoesNotExposeCredentialsInDiagnostics (requires the isolated Facebook TLS fixture and trusted test certificate)' >&2
fail "the isolated harness did not execute every backend provider fixture test; adapt those tests to inject the Compose services before treating this run as a passing backend suite."
