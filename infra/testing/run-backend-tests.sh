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
socat TCP-LISTEN:44229,bind=127.0.0.1,reuseaddr,fork TCP:nats:4222,connect-timeout=2 \
    >/dev/null 2>&1 &
nats_proxy_pid=$!
trap 'kill "$postgres_proxy_pid" "$nats_proxy_pid" 2>/dev/null || true' EXIT HUP INT TERM
wait_for_service ingestion-nats-loopback 127.0.0.1 44229
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
# Do not copy Windows build trees across the Docker bind mount only to delete
# them afterwards. Native/mobile outputs can dwarf the actual source snapshot.
find /workspace/src /workspace/tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
tar -C /source --exclude=bin --exclude=obj -cf - src tests | tar -C /workspace -xf -
cp -a /package-assets/src/. /workspace/src/
cp -a /package-assets/tests/. /workspace/tests/

cd /workspace

# SQLAUTH fixtures deliberately do not migrate shared state during each test.
# Provision this run's private database through the same deployment entrypoint
# used by Compose, then install its reviewed authority-lock fixture profile.
dotnet build tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj \
    --configuration Release --no-restore -m:1 -nr:false -p:WasmBuildNative=false
ConnectionStrings__SwiftAid="$SWIFTAID_TEST_POSTGRES_CONNECTION" \
    dotnet src/SwiftAid.Gateway/bin/Release/net10.0/SwiftAid.Gateway.dll --migrate-database
PGPASSWORD=swiftaid-local-fixture-p21-only psql -h postgres -U p21_migration_owner -d swiftaid_p21a \
    -v ON_ERROR_STOP=1 -v helper_owner=p21_helper_owner -v runtime_role=p21_lock_runtime \
    -f /workspace/infra/postgres/case-authority-lock-helper.sql
PGPASSWORD=swiftaid-local-fixture-p21-only psql -h postgres -U p21_migration_owner -d swiftaid_p21a \
    -v ON_ERROR_STOP=1 -c 'GRANT EXECUTE ON FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean) TO p21_migration_owner;'

# Finish independent suites even if one fails, retaining every TRX result.
set +e

dotnet test tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj \
    --configuration Release \
    --no-restore \
    --results-directory /test-output/domain \
    --logger "trx;LogFileName=SwiftAid.Domain.Tests.trx"
domain_status=$?

dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj \
    --configuration Release \
    --no-restore \
    --results-directory /test-output/application \
    --logger "trx;LogFileName=SwiftAid.Application.Tests.trx"
application_status=$?

dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj \
    --configuration Release \
    --no-restore \
    --filter 'FullyQualifiedName!~ExternalIdentityMappingPolicyTests&FullyQualifiedName!~IdentityEndpointSecurityTests' \
    --results-directory /test-output/integration \
    --logger "trx;LogFileName=SwiftAid.Integration.Tests.trx"
integration_status=$?

env -u SWIFTAID_RUN_SQLAUTH_FIXTURES \
    -u SWIFTAID_TEST_POSTGRES_CONNECTION \
    SWIFTAID_RUN_IDENTITY_POSTGRES_FIXTURES=1 \
    dotnet test tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj \
        --configuration Release \
        --no-restore \
        --filter 'FullyQualifiedName~ExternalIdentityMappingPolicyTests|FullyQualifiedName~IdentityEndpointSecurityTests' \
        --results-directory /test-output/identity \
        --logger "trx;LogFileName=SwiftAid.Identity.Postgres.Tests.trx"
identity_status=$?
set -e

printf '%s\n' \
    'SQLAUTH and identity PostgreSQL fixtures ran in separate passes because the existing test guards intentionally reject simultaneous SQLAUTH and identity fixture flags.' \
    'Scope: isolated backend fixtures only. TRX files record actual skipped tests. Docker-owning provider fixtures, live models, TLS identity, load and browser/device scenarios require separate opt-in runs; this is not full-system acceptance.'
printf 'Suite exit codes: domain=%s application=%s integration=%s identity=%s\n' \
    "$domain_status" "$application_status" "$integration_status" "$identity_status"
[ "$domain_status" -eq 0 ] && [ "$application_status" -eq 0 ] && \
    [ "$integration_status" -eq 0 ] && [ "$identity_status" -eq 0 ] || fail "one or more backend suites failed; inspect retained TRX results."
