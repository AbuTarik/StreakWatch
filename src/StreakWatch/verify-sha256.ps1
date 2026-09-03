param(
    [string]$Path = ".\publish"
)

$ErrorActionPreference = "Stop"
$sumFile = Join-Path $Path "SHA256SUMS.txt"

if (-not (Test-Path $sumFile)) {
    throw "SHA256SUMS.txt not found in $Path"
}

$failed = $false
foreach ($line in Get-Content $sumFile) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split "\s{2,}", 2
    if ($parts.Count -ne 2) { continue }

    $expected = $parts[0].Trim().ToLowerInvariant()
    $relative = $parts[1].Trim()
    $file = Join-Path $Path $relative

    if (-not (Test-Path $file)) {
        Write-Host "MISSING  $relative"
        $failed = $true
        continue
    }

    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -eq $expected) {
        Write-Host "OK       $relative"
    } else {
        Write-Host "FAILED   $relative"
        $failed = $true
    }
}

if ($failed) { exit 1 }
Write-Host "All SHA-256 checks passed."
