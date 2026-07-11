param(
  [string]$Version = "",
  [string]$OutputRoot = "",
  [string]$Runtime = "win-x64",
  [switch]$SelfContained,
  [switch]$FrameworkDependent,
  [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repoRoot "apps\windows\Ipi.Desktop\Ipi.Desktop.csproj"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
  $OutputRoot = Join-Path $repoRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
  $commit = "dev"
  try {
    $commit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
    if ([string]::IsNullOrWhiteSpace($commit)) { $commit = "dev" }
  } catch { }
  $Version = "0.1.0-$commit"
}

$selfContainedValue = if ($FrameworkDependent) { "false" } else { "true" }
if ($SelfContained) { $selfContainedValue = "true" }
$packageName = "ipi-portable-$Version-$Runtime"
$stageRoot = Join-Path $OutputRoot $packageName
$appDir = Join-Path $stageRoot "ipi"
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$shaPath = "$zipPath.sha256"
$manifestPath = Join-Path $stageRoot "ipi-portable-manifest.json"

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

if (-not $NoBuild) {
  if (Test-Path $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
  New-Item -ItemType Directory -Path $appDir -Force | Out-Null
  $publishArgs = @(
    "publish",
    $project,
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $selfContainedValue,
    "-o", $appDir
  )
  & dotnet @publishArgs
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}
elseif (-not (Test-Path -LiteralPath $appDir -PathType Container)) {
  throw "-NoBuild requires an existing staged app directory: $appDir"
}

Get-ChildItem -LiteralPath $appDir -Recurse -File |
  Where-Object { $_.Extension -ieq ".pdb" } |
  Remove-Item -Force

$requiredAppFiles = @(
  "ipi.exe",
  "agent-bridge.mjs",
  "approval-router.mjs",
  "bridge-policy.mjs",
  "package-bridge.mjs",
  "ipi-apply-update.ps1"
)
$missingAppFiles = $requiredAppFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $appDir $_) -PathType Leaf) }
if ($missingAppFiles.Count -gt 0) {
  throw "Published app payload is incomplete: $($missingAppFiles -join ', ')"
}
$payloadFiles = @(Get-ChildItem -LiteralPath $appDir -Recurse -File)
$payloadBytes = ($payloadFiles | Measure-Object Length -Sum).Sum
if ($payloadFiles.Count -lt 10 -or $payloadBytes -lt 1MB) {
  throw "Published app payload is unexpectedly small: $($payloadFiles.Count) files, $payloadBytes bytes"
}

$setupCmd = @'
@echo off
setlocal
pushd "%~dp0ipi"
start "" "%~dp0ipi\ipi.exe" --setup
popd
'@
$startCmd = @'
@echo off
setlocal
pushd "%~dp0ipi"
start "" "%~dp0ipi\ipi.exe"
popd
'@
$setupCmd | Set-Content -Path (Join-Path $stageRoot "Setup ipi.cmd") -Encoding ASCII
$startCmd | Set-Content -Path (Join-Path $stageRoot "Start ipi.cmd") -Encoding ASCII

$commitFull = ""
try { $commitFull = (& git -C $repoRoot rev-parse HEAD).Trim() } catch { }
$manifest = [ordered]@{
  app = "ipi"
  package = $packageName
  version = $Version
  runtime = $Runtime
  selfContained = [bool]::Parse($selfContainedValue)
  createdAt = (Get-Date).ToString("O")
  gitCommit = $commitFull
  appDir = "ipi"
  setupCommand = "Setup ipi.cmd"
  startCommand = "Start ipi.cmd"
  directSetup = "ipi\\ipi.exe --setup"
  directStart = "ipi\\ipi.exe"
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $zipPath -Force
$zipItem = Get-Item -LiteralPath $zipPath
if ($zipItem.Length -lt 1MB) { throw "Portable archive is unexpectedly small: $($zipItem.Length) bytes" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
  if (-not ($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq "ipi/ipi.exe" } | Select-Object -First 1)) {
    throw "Portable archive does not contain ipi/ipi.exe"
  }
}
finally {
  $archive.Dispose()
}
$hash = Get-FileHash -Algorithm SHA256 -Path $zipPath
"$($hash.Hash)  $(Split-Path $zipPath -Leaf)" | Set-Content -Path $shaPath -Encoding ASCII

Write-Host "Portable release created:" -ForegroundColor Green
Write-Host "  Folder: $stageRoot" -ForegroundColor Cyan
Write-Host "  Zip:    $zipPath" -ForegroundColor Cyan
Write-Host "  SHA256: $($hash.Hash)" -ForegroundColor Cyan

[pscustomobject]@{
  Folder = $stageRoot
  Zip = $zipPath
  Sha256 = $hash.Hash
  Manifest = $manifestPath
}
