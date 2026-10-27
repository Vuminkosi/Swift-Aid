FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS package-fetch

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

WORKDIR /workspace

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src ./src
COPY tests ./tests

RUN dotnet restore tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj --locked-mode -p:NuGetAudit=false \
    && dotnet restore tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj --locked-mode -p:NuGetAudit=false \
    && dotnet restore tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj --locked-mode -p:NuGetAudit=false \
    && mkdir -p /package-assets/src /package-assets/tests \
    && for source_root in src tests; do \
        find "/workspace/$source_root" -type d -name obj -print | while read project_obj; do \
            relative_path=${project_obj#/workspace/}; \
            target_path="/package-assets/$relative_path"; \
            mkdir -p "$target_path"; \
            cp -a "$project_obj/." "$target_path/"; \
        done; \
    done

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS backend-test-runner

WORKDIR /workspace

RUN apt-get update \
    && apt-get install --yes --no-install-recommends socat \
    && rm -rf /var/lib/apt/lists/*

COPY --from=package-fetch /workspace /workspace
COPY --from=package-fetch /package-assets /package-assets
COPY infra/testing/run-backend-tests.sh /usr/local/bin/run-swiftaid-backend-tests
RUN chmod 0555 /usr/local/bin/run-swiftaid-backend-tests

ENTRYPOINT ["/bin/sh", "/usr/local/bin/run-swiftaid-backend-tests"]
