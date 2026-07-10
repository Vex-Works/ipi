param(
  [string]$Root = (Join-Path $env:TEMP "ipi-runtime-sandbox"),
  [string]$ExePath = "",
  [string]$NodePath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($ExePath)) {
  $ExePath = Join-Path $repoRoot "apps\windows\Ipi.Desktop\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ipi.exe"
}

if (!(Test-Path $ExePath)) {
  throw "ipi.exe not found: $ExePath. Run dotnet publish first."
}

$agentDir = Join-Path $Root "pi-agent"
$codexDir = Join-Path $Root "codex-skills"
if (!(Test-Path $agentDir)) {
  throw "Sandbox does not exist: $agentDir. Run New-IpiSandbox.ps1 first."
}

$oldPiAgentDir = $env:PI_AGENT_DIR
$oldCodexSkillsDir = $env:CODEX_SKILLS_DIR
$oldIpiNodePath = $env:IPI_NODE_PATH

try {
  $env:PI_AGENT_DIR = $agentDir
  $env:CODEX_SKILLS_DIR = $codexDir
  if (![string]::IsNullOrWhiteSpace($NodePath)) { $env:IPI_NODE_PATH = $NodePath }

  Write-Host "Launching ipi with isolated runtime:" -ForegroundColor Green
  Write-Host "  PI_AGENT_DIR=$env:PI_AGENT_DIR" -ForegroundColor Cyan
  Write-Host "  CODEX_SKILLS_DIR=$env:CODEX_SKILLS_DIR" -ForegroundColor Cyan
  if ($env:IPI_NODE_PATH) { Write-Host "  IPI_NODE_PATH=$env:IPI_NODE_PATH" -ForegroundColor Cyan }

  Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath)
}
finally {
  $env:PI_AGENT_DIR = $oldPiAgentDir
  $env:CODEX_SKILLS_DIR = $oldCodexSkillsDir
  $env:IPI_NODE_PATH = $oldIpiNodePath
}
