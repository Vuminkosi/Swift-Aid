[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Task,

    [string]$Configuration = 'Release',

    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repositoryRoot

try {
    if ($Task -notmatch '^ELL-T\d{2}$') {
        throw "Task must look like ELL-Tnn (received '$Task')."
    }

    $artifactsDir = Join-Path 'artifacts/ellai/tests' $Task
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    $resultFile = Join-Path $artifactsDir 'results.txt'

    $filter = "FullyQualifiedName~Ellai&Task=$Task"
    $output = dotnet test tests/SwiftAid.Application.Tests/SwiftAid.Application.Tests.csproj `
        --filter $filter -c $Configuration --nologo -v q 2>&1
    $output | Tee-Object -FilePath $resultFile | Write-Output

    if ($LASTEXITCODE -ne 0) {
        throw "Task $Task tests failed; see $resultFile."
    }

    if ($output -match 'Passed:\s+0[,\s]') {
        throw "Task $Task ran zero tests; the runner fails when zero tests run."
    }

    if ($Live) {
        throw "Task $Task -Live gate is blocked: no provider/device/infrastructure probes are configured in this environment. A mock pass is never reported as live."
    }

    Write-Output "Task $Task verification passed (live probes excluded; see $resultFile)."
}
finally {
    Pop-Location
}
