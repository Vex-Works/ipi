param(
  [string]$Root = (Join-Path $env:TEMP "ipi-runtime-sandbox")
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $Root)) {
  Write-Host "Sandbox does not exist: $Root" -ForegroundColor Yellow
  exit 0
}

Remove-Item -LiteralPath $Root -Recurse -Force
Write-Host "Removed sandbox: $Root" -ForegroundColor Green
