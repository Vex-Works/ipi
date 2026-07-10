param(
  [string]$Root = (Join-Path $env:TEMP "ipi-runtime-sandbox"),
  [switch]$Reset,
  [switch]$MinimalConfig
)

$ErrorActionPreference = "Stop"

if ($Reset -and (Test-Path $Root)) {
  Remove-Item -LiteralPath $Root -Recurse -Force
}

$agentDir = Join-Path $Root "pi-agent"
$codexDir = Join-Path $Root "codex-skills"
$dirs = @(
  $Root,
  $agentDir,
  (Join-Path $agentDir "sessions"),
  (Join-Path $agentDir "skills"),
  (Join-Path $agentDir "npm"),
  (Join-Path $agentDir "npm\node_modules"),
  $codexDir
)

foreach ($dir in $dirs) {
  New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$modelsPath = Join-Path $agentDir "models.json"
if (!(Test-Path $modelsPath)) {
  '{ "providers": {} }' | Set-Content -Path $modelsPath -Encoding UTF8
}

if ($MinimalConfig) {
  $settingsPath = Join-Path $agentDir "settings.json"
  if (!(Test-Path $settingsPath)) {
@'
{
  "defaultProvider": "",
  "defaultModel": "",
  "defaultThinkingLevel": "medium",
  "packages": []
}
'@ | Set-Content -Path $settingsPath -Encoding UTF8
  }
}

[pscustomobject]@{
  Root = $Root
  AgentDir = $agentDir
  CodexSkillDir = $codexDir
  SettingsJson = Join-Path $agentDir "settings.json"
  ModelsJson = $modelsPath
} | Format-List

Write-Host ""
Write-Host "Sandbox created. Launch with:" -ForegroundColor Green
Write-Host "  powershell -ExecutionPolicy Bypass -File scripts/runtime-test/Start-IpiSandbox.ps1 -Root `"$Root`"" -ForegroundColor Cyan
