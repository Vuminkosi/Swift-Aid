FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props SwiftAid.sln ./
COPY src ./src
COPY resources ./resources

RUN dotnet restore src/SwiftAid.Gateway/SwiftAid.Gateway.csproj --locked-mode
RUN dotnet publish src/SwiftAid.Gateway/SwiftAid.Gateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "SwiftAid.Gateway.dll"]
