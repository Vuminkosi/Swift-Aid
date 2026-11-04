[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    python -m unittest discover -s tests/bootstrap -v
    if ($LASTEXITCODE -ne 0) {
        throw 'SwiftAid bootstrap checks failed.'
    }

    if (-not (Test-Path 'SwiftAid.sln')) {
        throw 'SwiftAid.sln is missing.'
    }

    dotnet restore SwiftAid.sln --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked .NET restore failed.'
    }

    dotnet test SwiftAid.sln --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw '.NET tests failed.'
    }

    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
        throw 'uv is required for the locked Python worker check.'
    }
    if (-not (Test-Path 'workers/uv.lock')) {
        throw 'workers/uv.lock is missing; create the Python 3.12 lock before verification.'
    }
    uv sync --project workers --locked --all-groups
    if ($LASTEXITCODE -ne 0) {
        throw 'Locked Python worker sync failed.'
    }

    uv run --project workers --locked ruff check workers/src workers/tests
    if ($LASTEXITCODE -ne 0) {
        throw 'Python worker lint failed.'
    }

    uv run --project workers --locked pytest workers/tests -q
    if ($LASTEXITCODE -ne 0) {
        throw 'Python worker tests failed.'
    }

    $validationEnvironment = @{
        POSTGRES_DB = 'swiftaid_config_check'
        POSTGRES_USER = 'config_check'
        POSTGRES_PASSWORD = 'not-a-production-secret'
        QDRANT_API_KEY = 'not-a-production-secret'
        NATS_GATEWAY_USER = 'gateway_check'
        NATS_GATEWAY_PASSWORD = 'not-a-production-secret'
        NATS_WORKER_USER = 'worker_check'
        NATS_WORKER_PASSWORD = 'not-a-production-secret'
    }
    $previousEnvironment = @{}
    foreach ($name in $validationEnvironment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $validationEnvironment[$name], 'Process')
    }

    try {
        docker compose -f infra/compose.yml config --quiet
        if ($LASTEXITCODE -ne 0) {
            throw 'Docker Compose configuration is invalid.'
        }
    }
    finally {
        foreach ($name in $validationEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
}
finally {
    Pop-Location
}
