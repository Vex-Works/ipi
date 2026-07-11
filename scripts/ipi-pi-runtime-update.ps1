[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$ManagedRuntimeRoot,
  [Parameter(Mandatory = $true)][string]$NpmCommand,
  [Parameter(Mandatory = $true)][string]$PackageSpec,
  [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$ParentProcessId,
  [ValidateRange(30, 3600)][int]$NpmTimeoutSeconds = 600,
  [string]$LogPath = (Join-Path $env:TEMP ('ipi-pi-runtime-update-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '.log'))
)

$ErrorActionPreference = 'Stop'
$script:RuntimeRoot = $null

function Write-UpdateLog([string]$Message) {
  "[$(Get-Date -Format o)] $Message" | Tee-Object -FilePath $LogPath -Append
}

function Get-NormalizedFullPath([string]$Path) {
  if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Path is empty.' }
  $full = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($Path.Trim()))
  $root = [IO.Path]::GetPathRoot($full)
  if ($full.Length -gt $root.Length) { $full = $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) }
  return $full
}

function Test-PathWithin([string]$Root, [string]$Candidate) {
  if ([string]::Equals($Root, $Candidate, [StringComparison]::OrdinalIgnoreCase)) { return $true }
  $prefix = $Root
  if (-not $prefix.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [StringComparison]::Ordinal)) {
    $prefix += [IO.Path]::DirectorySeparatorChar
  }
  return $Candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NotReparsePoint([string]$Path) {
  $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Reparse points are not allowed in managed runtime paths: $Path"
  }
}

function Assert-NoReparsePointsInExistingPath([string]$Path) {
  $full = Get-NormalizedFullPath $Path
  $pathRoot = [IO.Path]::GetPathRoot($full)
  $current = $pathRoot
  if (Test-Path -LiteralPath $current) { Assert-NotReparsePoint $current }
  $remainder = $full.Substring($pathRoot.Length)
  foreach ($segment in $remainder.Split(@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
    $current = Join-Path $current $segment
    if (-not (Test-Path -LiteralPath $current)) { break }
    Assert-NotReparsePoint $current
  }
}

function Assert-ManagedPath([string]$Path) {
  $candidate = Get-NormalizedFullPath $Path
  if (-not (Test-PathWithin $script:RuntimeRoot $candidate)) { throw "Managed path escapes runtime root: $candidate" }
  Assert-NoReparsePointsInExistingPath $script:RuntimeRoot
  Assert-NoReparsePointsInExistingPath $candidate
  return $candidate
}

function Assert-NoReparseTree([string]$Path) {
  $root = Assert-ManagedPath $Path
  if (-not (Test-Path -LiteralPath $root)) { return }
  $pending = [Collections.Generic.Stack[string]]::new()
  $pending.Push($root)
  while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Managed runtime contains a reparse point: $current" }
    if (-not $item.PSIsContainer) { continue }
    foreach ($child in Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop) {
      if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Managed runtime contains a reparse point: $($child.FullName)" }
      if ($child.PSIsContainer) { $pending.Push($child.FullName) }
    }
  }
}

function Remove-SafeManagedDirectory([string]$Path) {
  $target = Assert-ManagedPath $Path
  if (-not (Test-Path -LiteralPath $target)) { return }
  $item = Get-Item -LiteralPath $target -Force
  if (-not $item.PSIsContainer) { throw "Expected a managed directory: $target" }
  Assert-NoReparseTree $target
  Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
}

function Stop-ProcessTree([Diagnostics.Process]$Process) {
  if ($Process.HasExited) { return }
  $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
  try {
    $killer = Start-Process -FilePath $taskKill -ArgumentList @('/PID', [string]$Process.Id, '/T', '/F') -WindowStyle Hidden -PassThru
    try {
      if (-not $killer.WaitForExit(10000)) { try { $killer.Kill() } catch {} }
    } finally {
      $killer.Dispose()
    }
  } catch {
    try { $Process.Kill() } catch {}
  }
  try { [void]$Process.WaitForExit(5000) } catch {}
}

function Invoke-ProcessWithTimeout(
  [string]$FilePath,
  [string[]]$ArgumentList,
  [string]$WorkingDirectory,
  [int]$TimeoutSeconds,
  [string]$Operation
) {
  $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru
  try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
      Stop-ProcessTree $process
      throw "$Operation timed out after $TimeoutSeconds seconds and its process tree was stopped."
    }
    if ($process.ExitCode -ne 0) { throw "$Operation failed with exit code $($process.ExitCode)." }
  } finally {
    $process.Dispose()
  }
}

function Assert-ManagedPiRuntime([string]$PiRuntimeDir, [string]$ExpectedVersion = '') {
  $runtime = Assert-ManagedPath $PiRuntimeDir
  Assert-NoReparseTree $runtime
  $rootPackageJson = Assert-ManagedPath (Join-Path $runtime 'package.json')
  if (-not (Test-Path -LiteralPath $rootPackageJson -PathType Leaf)) { throw "Managed Pi package.json is missing: $rootPackageJson" }
  $rootMetadata = Get-Content -LiteralPath $rootPackageJson -Raw | ConvertFrom-Json
  if ($rootMetadata.name -ne 'ipi-managed-pi-runtime') { throw "Unexpected managed Pi package name: $($rootMetadata.name)" }

  $agentRoot = Assert-ManagedPath (Join-Path $runtime 'node_modules\@earendil-works\pi-coding-agent')
  $agentPackageJson = Assert-ManagedPath (Join-Path $agentRoot 'package.json')
  $entryPoint = Assert-ManagedPath (Join-Path $agentRoot 'dist\index.js')
  if (-not (Test-Path -LiteralPath $agentPackageJson -PathType Leaf) -or -not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
    throw "Managed Pi runtime is incomplete: $agentRoot"
  }
  $agentMetadata = Get-Content -LiteralPath $agentPackageJson -Raw | ConvertFrom-Json
  if ($agentMetadata.name -ne '@earendil-works/pi-coding-agent') { throw "Unexpected installed package: $($agentMetadata.name)" }
  if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $agentMetadata.version -ne $ExpectedVersion) {
    throw "Installed Pi version $($agentMetadata.version) does not match expected version $ExpectedVersion."
  }
}

$stagingDir = $null
$rollbackDir = $null
$piRuntimeDir = $null
$restartAttempted = $false
$restartConfirmed = $false

try {
  $script:RuntimeRoot = Get-NormalizedFullPath $ManagedRuntimeRoot
  Assert-NoReparsePointsInExistingPath $script:RuntimeRoot
  if (-not (Test-Path -LiteralPath $script:RuntimeRoot -PathType Container)) { throw "Managed runtime root is missing: $script:RuntimeRoot" }
  Assert-NotReparsePoint $script:RuntimeRoot

  $nodeDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'node')
  $piRuntimeDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'pi')
  $downloadsDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'downloads')
  $extractDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'extract-node')
  $stagingDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'pi-update-staging')
  $rollbackDir = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'pi-update-rollback')
  foreach ($managedDirectory in @($nodeDir, $piRuntimeDir, $downloadsDir, $extractDir, $stagingDir, $rollbackDir)) {
    if (Test-Path -LiteralPath $managedDirectory) { Assert-NoReparseTree $managedDirectory }
  }

  if ($PackageSpec -notmatch '^@earendil-works/pi-coding-agent@[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "PackageSpec must use an exact pi-coding-agent version: $PackageSpec"
  }
  $packagePrefix = '@earendil-works/pi-coding-agent@'
  $expectedVersion = $PackageSpec.Substring($packagePrefix.Length)

  $expectedNpm = Assert-ManagedPath (Join-Path $nodeDir 'npm.cmd')
  $npmPath = Assert-ManagedPath $NpmCommand
  if (-not [string]::Equals($npmPath, $expectedNpm, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $npmPath -PathType Leaf)) {
    throw "Refusing a non-managed npm command: $npmPath"
  }
  Assert-NoReparseTree $nodeDir

  $markerPath = Assert-ManagedPath (Join-Path $script:RuntimeRoot 'ipi.runtime-owner.json')
  if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw "Managed runtime ownership marker is missing: $markerPath" }
  $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
  $markerRoot = Get-NormalizedFullPath ([string]$marker.managedRoot)
  if ($marker.owner -ne 'ipi' -or $marker.kind -ne 'managed-runtime' -or -not [string]::Equals($markerRoot, $script:RuntimeRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Managed runtime ownership marker is invalid.'
  }

  $exe = Get-NormalizedFullPath $ExePath
  if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "ipi executable is missing: $exe" }

  Write-UpdateLog "waiting for ipi pid=$ParentProcessId"
  $parentProcess = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
  if ($null -ne $parentProcess) {
    try {
      if (-not $parentProcess.WaitForExit(45000)) { throw "ipi pid=$ParentProcessId did not exit before timeout" }
    } finally {
      $parentProcess.Dispose()
    }
  }

  # Recover a prior interrupted directory swap before starting a new update.
  if (Test-Path -LiteralPath $rollbackDir) {
    Assert-NoReparseTree $rollbackDir
    if (Test-Path -LiteralPath $piRuntimeDir) {
      try {
        Assert-ManagedPiRuntime $piRuntimeDir
        Remove-SafeManagedDirectory $rollbackDir
      } catch {
        Remove-SafeManagedDirectory $piRuntimeDir
        Move-Item -LiteralPath $rollbackDir -Destination $piRuntimeDir -ErrorAction Stop
      }
    } else {
      Move-Item -LiteralPath $rollbackDir -Destination $piRuntimeDir -ErrorAction Stop
    }
  }
  if (Test-Path -LiteralPath $stagingDir) { Remove-SafeManagedDirectory $stagingDir }
  Assert-ManagedPiRuntime $piRuntimeDir

  New-Item -ItemType Directory -Path $stagingDir -ErrorAction Stop | Out-Null
  Assert-ManagedPath $stagingDir | Out-Null
  $stagingPackageJson = Assert-ManagedPath (Join-Path $stagingDir 'package.json')
  $stagingMetadata = @{ name = 'ipi-managed-pi-runtime'; private = $true; description = 'ipi-managed upstream Pi runtime.' } | ConvertTo-Json
  [IO.File]::WriteAllText($stagingPackageJson, $stagingMetadata, [Text.UTF8Encoding]::new($false))

  Write-UpdateLog "staging managed npm install $PackageSpec"
  Invoke-ProcessWithTimeout $npmPath @('install', '--ignore-scripts', '--no-audit', '--no-fund', $PackageSpec) $stagingDir $NpmTimeoutSeconds 'managed npm install'
  Assert-ManagedPiRuntime $stagingDir $expectedVersion

  Move-Item -LiteralPath $piRuntimeDir -Destination $rollbackDir -ErrorAction Stop
  try {
    Move-Item -LiteralPath $stagingDir -Destination $piRuntimeDir -ErrorAction Stop
    Assert-ManagedPiRuntime $piRuntimeDir $expectedVersion
  } catch {
    if (Test-Path -LiteralPath $piRuntimeDir) { Remove-SafeManagedDirectory $piRuntimeDir }
    if (Test-Path -LiteralPath $rollbackDir) { Move-Item -LiteralPath $rollbackDir -Destination $piRuntimeDir -ErrorAction Stop }
    throw
  }

  Write-UpdateLog 'managed Pi runtime update verified; restarting ipi'
  $restartAttempted = $true
  $newAppProcess = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
  try {
    if ($newAppProcess.WaitForExit(5000)) {
      throw "updated ipi exited during startup with code $($newAppProcess.ExitCode)"
    }
    $restartConfirmed = $true
  } finally {
    $newAppProcess.Dispose()
  }
  try { if (Test-Path -LiteralPath $rollbackDir) { Remove-SafeManagedDirectory $rollbackDir } }
  catch { Write-UpdateLog ("update succeeded but rollback cleanup was deferred: " + $_.Exception.Message) }
} catch {
  $failure = $_.Exception.Message
  $rollbackRestored = $false
  if (-not $restartConfirmed) {
    try {
      if ($null -ne $rollbackDir -and (Test-Path -LiteralPath $rollbackDir)) {
        if ($null -ne $piRuntimeDir -and (Test-Path -LiteralPath $piRuntimeDir)) { Remove-SafeManagedDirectory $piRuntimeDir }
        Move-Item -LiteralPath $rollbackDir -Destination $piRuntimeDir -ErrorAction Stop
        Assert-ManagedPiRuntime $piRuntimeDir
        $rollbackRestored = $true
      }
      if ($null -ne $stagingDir -and (Test-Path -LiteralPath $stagingDir)) { Remove-SafeManagedDirectory $stagingDir }
    } catch {
      $failure += "; rollback failed: $($_.Exception.Message)"
    }
  }
  if ($restartAttempted -and $rollbackRestored) {
    try {
      Write-UpdateLog 'updated ipi failed startup; restarting ipi with the restored runtime'
      $oldAppProcess = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
      try {
        if ($oldAppProcess.WaitForExit(5000)) { $failure += "; restored ipi exited during startup with code $($oldAppProcess.ExitCode)" }
      } finally {
        $oldAppProcess.Dispose()
      }
    } catch {
      $failure += "; restored ipi could not be restarted: $($_.Exception.Message)"
    }
  }
  $restartOutcome = if ($restartAttempted -and $rollbackRestored) { 'restored ipi restart attempted' } else { 'ipi will not restart' }
  Write-UpdateLog ("update failed; old runtime restored when possible; $restartOutcome`: " + $failure)
  exit 1
}
