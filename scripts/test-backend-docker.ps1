[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repositoryRoot 'infra/testing/compose.yml'
$environmentTemplate = Join-Path $repositoryRoot '.env.example'
$resultsRoot = Join-Path $repositoryRoot 'artifacts/backend-tests'
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N'))
$resultsDirectory = Join-Path $resultsRoot $runId
$projectName = "swiftaid-backend-tests-$($runId.Replace('-', '').ToLowerInvariant())"

foreach ($requiredPath in @($composeFile, $environmentTemplate, (Join-Path $repositoryRoot 'global.json'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required Docker backend test input is missing: $requiredPath"
    }
}

$sdkVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$dockerfile = Get-Content -LiteralPath (Join-Path $repositoryRoot 'infra/testing/Dockerfile') -Raw
if ($dockerfile -notmatch [regex]::Escape("mcr.microsoft.com/dotnet/sdk:$sdkVersion")) {
    throw "The Docker test SDK must match the pinned global.json SDK version $sdkVersion."
}

$testProjects = @(
    'tests/SwiftAid.Domain.Tests/SwiftAid.Domain.Tests.csproj',
    'tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj',
    'tests/SwiftAid.Integration.Tests/SwiftAid.Integration.Tests.csproj'
)
foreach ($project in $testProjects) {
    $projectPath = Join-Path $repositoryRoot $project
    $lockPath = Join-Path (Split-Path -Parent $projectPath) 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw "Backend test project and its locked package file are required: $project"
    }
}

New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$previousResultsDirectory = [Environment]::GetEnvironmentVariable('SWIFTAID_BACKEND_TEST_RESULTS_DIR', 'Process')
[Environment]::SetEnvironmentVariable('SWIFTAID_BACKEND_TEST_RESULTS_DIR', $resultsDirectory, 'Process')

$composeArguments = @(
    '--project-name', $projectName,
    '--env-file', $environmentTemplate,
    '--file', $composeFile
)

try {
    docker info --format '{{.ServerVersion}}' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'A reachable Docker Engine is required to run the isolated backend fixtures.'
    }

    docker compose @composeArguments config --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'The isolated Docker backend test Compose configuration is invalid.'
    }

    Write-Host "Running locked backend tests with .NET SDK $sdkVersion in isolated PostgreSQL, Redis, and NATS fixtures."
    docker compose @composeArguments up `
        --build `
        --abort-on-container-exit `
        --exit-code-from backend-tests
    if ($LASTEXITCODE -ne 0) {
        throw "Docker backend tests failed with exit code $LASTEXITCODE. Results: $resultsDirectory"
    }

    Write-Host "Docker backend tests passed. Results retained at: $resultsDirectory"
}
finally {
    try {
        docker compose @composeArguments down --remove-orphans
        $cleanupExitCode = $LASTEXITCODE
    }
    catch {
        $cleanupExitCode = 1
    }
    finally {
        [Environment]::SetEnvironmentVariable('SWIFTAID_BACKEND_TEST_RESULTS_DIR', $previousResultsDirectory, 'Process')
    }

    if ($cleanupExitCode -ne 0) {
        Write-Warning "Could not remove this run's isolated containers and network (project $projectName). No volumes are declared by the fixture Compose file."
    }
}
