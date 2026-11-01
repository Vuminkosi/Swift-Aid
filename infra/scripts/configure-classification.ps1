param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$OrganizationIds
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$workerFile = Join-Path $repository '.classification-worker.env'
$gatewayFile = Join-Path $repository '.classification-gateway.env'
if ($OrganizationIds.Count -gt 100 -or ($OrganizationIds | Where-Object { $_ -notmatch '^[A-Za-z0-9._-]{1,200}$' })) {
    throw 'Supply 1-100 explicit organization identifiers containing letters, digits, dots, underscores or hyphens.'
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $workerFile)) {
    $bytes = New-Object byte[] 48
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes)
    [IO.File]::WriteAllText($workerFile, "CLASSIFICATION_WORKER_TOKEN=$token`n", $utf8)
}
$lines = @(Get-Content -LiteralPath $workerFile | Where-Object { $_ -like 'CLASSIFICATION_WORKER_TOKEN=*' })
if ($lines.Count -ne 1) { throw 'The existing worker file must contain exactly one worker token.' }
$token = $lines[0].Substring('CLASSIFICATION_WORKER_TOKEN='.Length)
if ($token.Length -lt 32 -or $token.Length -gt 240) { throw 'Invalid worker token length.' }
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token)))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
$config = @('Classification__Enabled=true', "Classification__WorkerTokenSha256=$hash")
for ($i = 0; $i -lt $OrganizationIds.Count; $i++) { $config += "Classification__Organizations__${i}=$($OrganizationIds[$i])" }
[IO.File]::WriteAllText($gatewayFile, ($config -join "`n") + "`n", $utf8)
Write-Output 'Prepared ignored local classification credentials. Apply migrations before enabling ingestion dispatch.'
