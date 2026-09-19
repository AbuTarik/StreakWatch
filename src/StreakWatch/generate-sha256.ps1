param(
    [string]$Path = ".\publish"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) {
    throw "Path not found: $Path"
}

$files = Get-ChildItem $Path -File -Recurse |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName

$outFile = Join-Path $Path "SHA256SUMS.txt"
$lines = foreach ($file in $files) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $basePath = (Resolve-Path $Path).Path.TrimEnd([char[]]"\\/")
    $relative = $file.FullName.Substring($basePath.Length).TrimStart([char[]]"\\/")
    "$hash  $relative"
}

$lines | Set-Content -Encoding ascii $outFile
Write-Host "Created: $outFile"
