param(
  [string]$Version = "",
  [string]$OutputRoot = "",
  [string]$Runtime = "win-x64",
  [switch]$SelfContained,
  [switch]$FrameworkDependent,
  [string]$MakensisPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$project = Join-Path $repoRoot "apps\windows\Ipi.Desktop\Ipi.Desktop.csproj"
$template = Join-Path $PSScriptRoot "ipi.nsi.template"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
  $OutputRoot = Join-Path $repoRoot "dist"
}

if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
  $cmd = Get-Command "makensis.exe" -ErrorAction SilentlyContinue
  if ($cmd) { $MakensisPath = $cmd.Source }
}
if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
  $standardCandidates = @(
    (Join-Path $env:ProgramFiles "NSIS\makensis.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "NSIS\makensis.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\NSIS\makensis.exe")
  )
  foreach ($candidate in $standardCandidates) {
    if (Test-Path $candidate) { $MakensisPath = $candidate; break }
  }
}
if ([string]::IsNullOrWhiteSpace($MakensisPath) -or !(Test-Path $MakensisPath)) {
  throw "makensis.exe was not found. Install NSIS or pass -MakensisPath. This script does not download or install tools."
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
$installerRoot = Join-Path $OutputRoot "installer"
$stageAppDir = Join-Path $installerRoot "stage\ipi"
$generatedNsi = Join-Path $installerRoot "ipi.generated.nsi"
$outputExe = Join-Path $OutputRoot "ipi-Setup-$Version-$Runtime.exe"

if (Test-Path (Join-Path $installerRoot "stage")) { Remove-Item -LiteralPath (Join-Path $installerRoot "stage") -Recurse -Force }
New-Item -ItemType Directory -Path $stageAppDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null

$publishArgs = @(
  "publish",
  $project,
  "-c", "Release",
  "-r", $Runtime,
  "--self-contained", $selfContainedValue,
  "-o", $stageAppDir
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

function Get-RelativePath([string]$BasePath, [string]$Path) {
  $baseFull = [System.IO.Path]::GetFullPath($BasePath).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
  $pathFull = [System.IO.Path]::GetFullPath($Path)
  $baseUri = New-Object System.Uri($baseFull)
  $pathUri = New-Object System.Uri($pathFull)
  return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\\')
}

function New-UninstallDeleteCommands([string]$Root) {
  $lines = New-Object System.Collections.Generic.List[string]
  $files = Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName -Descending
  foreach ($file in $files) {
    $relative = Get-RelativePath $Root $file.FullName
    $lines.Add("  Delete `"`$INSTDIR\\$relative`"")
  }
  $lines.Add("  Delete `"`$INSTDIR\\Uninstall ipi.exe`"")
  $directories = Get-ChildItem -LiteralPath $Root -Directory -Recurse | Sort-Object FullName -Descending
  foreach ($directory in $directories) {
    $relative = Get-RelativePath $Root $directory.FullName
    $lines.Add("  RMDir `"`$INSTDIR\\$relative`"")
  }
  return ($lines -join [Environment]::NewLine)
}

$nsi = Get-Content -Path $template -Raw
$nsi = $nsi.Replace("@@APP_VERSION@@", $Version)
$nsi = $nsi.Replace("@@SOURCE_APP_DIR@@", $stageAppDir)
$nsi = $nsi.Replace("@@OUTPUT_EXE@@", $outputExe)
$nsi = $nsi.Replace("@@UNINSTALL_DELETE_COMMANDS@@", (New-UninstallDeleteCommands $stageAppDir))
$nsi | Set-Content -Path $generatedNsi -Encoding UTF8

& $MakensisPath $generatedNsi
if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE" }
if (!(Test-Path $outputExe)) { throw "Installer was not created: $outputExe" }

$hash = Get-FileHash -Algorithm SHA256 -Path $outputExe
"$($hash.Hash)  $(Split-Path $outputExe -Leaf)" | Set-Content -Path "$outputExe.sha256" -Encoding ASCII

Write-Host "NSIS installer created:" -ForegroundColor Green
Write-Host "  $outputExe" -ForegroundColor Cyan
Write-Host "  SHA256: $($hash.Hash)" -ForegroundColor Cyan

[pscustomobject]@{
  Installer = $outputExe
  Sha256 = $hash.Hash
  GeneratedScript = $generatedNsi
}
