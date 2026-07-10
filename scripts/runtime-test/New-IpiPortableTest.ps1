param(
  [string]$Root = (Join-Path $env:TEMP "ipi-portable-test"),
  [string]$PublishDir = "",
  [switch]$Reset,
  [switch]$WithBundledRuntime,
  [switch]$Launch
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
  $PublishDir = Join-Path $repoRoot "apps\windows\Ipi.Desktop\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
}

if (!(Test-Path $PublishDir)) {
  throw "Publish directory not found: $PublishDir. Run dotnet publish first."
}

$appDir = Join-Path $Root "app"
if ($Reset -and (Test-Path $Root)) {
  Remove-Item -LiteralPath $Root -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null

Copy-Item -Path (Join-Path $PublishDir "*") -Destination $appDir -Recurse -Force

if ($WithBundledRuntime) {
  $runtimeAgent = Join-Path $appDir "runtime\pi-agent"
  New-Item -ItemType Directory -Path (Join-Path $runtimeAgent "sessions") -Force | Out-Null
  New-Item -ItemType Directory -Path (Join-Path $runtimeAgent "npm\node_modules") -Force | Out-Null
  New-Item -ItemType Directory -Path (Join-Path $runtimeAgent "skills") -Force | Out-Null
  '{ "providers": {} }' | Set-Content -Path (Join-Path $runtimeAgent "models.json") -Encoding UTF8
@'
{
  "defaultProvider": "",
  "defaultModel": "",
  "defaultThinkingLevel": "medium",
  "packages": []
}
'@ | Set-Content -Path (Join-Path $runtimeAgent "settings.json") -Encoding UTF8
}

Write-Host "Portable test app created:" -ForegroundColor Green
Write-Host "  $appDir" -ForegroundColor Cyan

if ($Launch) {
  Start-Process -FilePath (Join-Path $appDir "ipi.exe") -WorkingDirectory $appDir
}
