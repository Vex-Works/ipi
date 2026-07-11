param(
    [Parameter(Mandatory = $true)]
    [int]$ParentProcessId,

    [Parameter(Mandatory = $true)]
    [string]$AppCurrent,

    [Parameter(Mandatory = $true)]
    [string]$AppExecutableName,

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [switch]$ApplyApp,
    [string]$ExpectedAppCommit = '',
    [string]$AppStage = '',
    [string]$AppBackup = '',

    [switch]$ApplyPi,
    [string]$ManagedRuntimeRoot = '',
    [string]$ExpectedPiPackage = '',
    [string]$ExpectedPiVersion = '',
    [string]$PiCurrent = '',
    [string]$PiStage = '',
    [string]$PiBackup = ''
)

$ErrorActionPreference = 'Stop'
$appOldBackedUp = $false
$appNewActive = $false
$piOldBackedUp = $false
$piNewActive = $false
$restartedProcess = $null

function Write-UpdateLog([string]$Message) {
    try {
        $entry = "[$([DateTime]::UtcNow.ToString('o'))] $Message"
        Add-Content -LiteralPath $LogPath -Value $entry -Encoding UTF8 -ErrorAction Stop
    }
    catch {
        # Logging is best-effort and must never interrupt swap or rollback.
    }
}

function Get-NormalizedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    if ([string]::Equals($full, $root, [StringComparison]::OrdinalIgnoreCase)) { return $full }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Test-PathsEqual([string]$Left, [string]$Right) {
    return [string]::Equals((Get-NormalizedPath $Left), (Get-NormalizedPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NotReparsePoint([string]$Path, [string]$Label) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must not be a reparse point: $Path"
    }
}

function Assert-PathAncestorsNotReparse([string]$Path, [string]$Label) {
    $current = Get-NormalizedPath $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            Assert-NotReparsePoint $current $Label
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $next = Get-NormalizedPath $parent.FullName
        if (Test-PathsEqual $next $current) { break }
        $current = $next
    }
}

function Assert-TreeNotReparse([string]$Root, [string]$Label) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push((Get-NormalizedPath $Root))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        Assert-NotReparsePoint $current $Label
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
            $item = Get-Item -LiteralPath $entry -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Label contains a reparse point: $entry"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Wait-ForProcessExit([int]$ProcessId, [int]$TimeoutMilliseconds) {
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $true }
    try { return $process.WaitForExit($TimeoutMilliseconds) }
    finally { $process.Dispose() }
}

function Assert-CurrentRoot([string]$Current, [string]$ExpectedParent, [string]$Label) {
    $currentFull = Get-NormalizedPath $Current
    $parentFull = Get-NormalizedPath $ExpectedParent
    if (-not (Test-Path -LiteralPath $currentFull -PathType Container)) {
        throw "$Label is missing: $currentFull"
    }
    if (-not (Test-PathsEqual ([IO.Path]::GetDirectoryName($currentFull)) $parentFull)) {
        throw "$Label is outside its expected parent: $currentFull"
    }
    Assert-PathAncestorsNotReparse $parentFull "$Label parent"
    Assert-PathAncestorsNotReparse $currentFull $Label
}

function Assert-SwapPath([string]$Candidate, [string]$ExpectedParent, [string]$RequiredPrefix, [bool]$MustExist, [string]$Label) {
    $candidateFull = Get-NormalizedPath $Candidate
    $parentFull = Get-NormalizedPath $ExpectedParent
    if (-not (Test-PathsEqual ([IO.Path]::GetDirectoryName($candidateFull)) $parentFull)) {
        throw "$Label is outside its expected parent: $candidateFull"
    }
    if (-not [IO.Path]::GetFileName($candidateFull).StartsWith($RequiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label does not use the required prefix: $candidateFull"
    }
    $exists = Test-Path -LiteralPath $candidateFull
    if ($MustExist -and -not $exists) { throw "$Label is missing: $candidateFull" }
    if (-not $MustExist -and $exists) { throw "$Label already exists: $candidateFull" }
    Assert-PathAncestorsNotReparse $parentFull "$Label parent"
    if ($exists) { Assert-PathAncestorsNotReparse $candidateFull $Label }
}

function Assert-AppSwapReady {
    $appParent = [IO.Path]::GetDirectoryName((Get-NormalizedPath $AppCurrent))
    Assert-CurrentRoot $AppCurrent $appParent 'app root'
    if ($ApplyApp) {
        if ($ExpectedAppCommit -notmatch '^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?$') {
            throw 'expected app commit is invalid'
        }
        Assert-SwapPath $AppStage $appParent '.ipi-app-staging-' $true 'app staging'
        Assert-SwapPath $AppBackup $appParent '.ipi-app-backup-' $false 'app backup'
        Assert-TreeNotReparse $AppStage 'app staging'
        $stagedExecutable = Join-Path $AppStage $AppExecutableName
        if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf)) {
            throw "staged app executable is missing: $stagedExecutable"
        }
        foreach ($requiredFile in @('agent-bridge.mjs', 'approval-router.mjs', 'bridge-policy.mjs', 'package-bridge.mjs', 'ipi-apply-update.ps1')) {
            if (-not (Test-Path -LiteralPath (Join-Path $AppStage $requiredFile) -PathType Leaf)) {
                throw "staged app is missing required runtime file: $requiredFile"
            }
        }
        $buildMarkerPath = Join-Path $AppStage 'ipi.build.json'
        if (-not (Test-Path -LiteralPath $buildMarkerPath -PathType Leaf)) { throw 'staged app build marker is missing' }
        $buildMarker = Get-Content -LiteralPath $buildMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (([string]$buildMarker.sourceCommit) -ne $ExpectedAppCommit -or
            ([string]$buildMarker.executableSha256) -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'staged app build marker is invalid'
        }
        $actualExecutableHash = (Get-FileHash -LiteralPath $stagedExecutable -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualExecutableHash, [string]$buildMarker.executableSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'staged app executable hash does not match its build marker'
        }
    }
}

function Assert-PiSwapReady {
    if (-not $ApplyPi) { return }
    if (-not [string]::Equals($ExpectedPiPackage, '@earendil-works/pi-coding-agent', [StringComparison]::Ordinal) -or
        $ExpectedPiVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw 'expected Pi package identity/version is invalid'
    }

    $managedRoot = Get-NormalizedPath $ManagedRuntimeRoot
    Assert-PathAncestorsNotReparse $managedRoot 'managed runtime root'
    $expectedPiCurrent = Get-NormalizedPath (Join-Path $managedRoot 'pi')
    if (-not (Test-PathsEqual $PiCurrent $expectedPiCurrent)) {
        throw "PiCurrent is not the fixed managed runtime child: $PiCurrent"
    }

    $piParent = [IO.Path]::GetDirectoryName((Get-NormalizedPath $PiCurrent))
    if (-not (Test-PathsEqual $piParent $managedRoot)) {
        throw "PiCurrent parent does not match ManagedRuntimeRoot: $PiCurrent"
    }
    Assert-CurrentRoot $PiCurrent $managedRoot 'Pi root'
    Assert-SwapPath $PiStage $managedRoot '.ipi-pi-staging-' $true 'Pi staging'
    Assert-SwapPath $PiBackup $managedRoot '.ipi-pi-backup-' $false 'Pi backup'
    Assert-TreeNotReparse $PiStage 'Pi staging'

    $markerPath = Join-Path $managedRoot 'ipi.runtime-owner.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'managed runtime ownership marker is missing' }
    Assert-PathAncestorsNotReparse $markerPath 'managed runtime ownership marker'
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.owner -ne 'ipi' -or $marker.kind -ne 'managed-runtime' -or -not (Test-PathsEqual ([string]$marker.managedRoot) $managedRoot)) {
        throw 'managed runtime ownership marker is invalid'
    }

    $stageManifestPath = Join-Path $PiStage 'package.json'
    if (-not (Test-Path -LiteralPath $stageManifestPath -PathType Leaf)) { throw 'Pi staging root package.json is missing' }
    $stageManifest = Get-Content -LiteralPath $stageManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $dependency = $stageManifest.dependencies.PSObject.Properties[$ExpectedPiPackage]
    if ($stageManifest.name -ne 'ipi-managed-pi-runtime' -or $stageManifest.private -ne $true -or
        $null -eq $dependency -or [string]$dependency.Value -ne $ExpectedPiVersion) {
        throw 'Pi staging root package.json is not the expected ipi-managed exact dependency'
    }

    $installedRoot = Join-Path $PiStage 'node_modules\@earendil-works\pi-coding-agent'
    $installedManifestPath = Join-Path $installedRoot 'package.json'
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) { throw 'staged Pi package.json is missing' }
    $installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($installedManifest.name -ne $ExpectedPiPackage -or $installedManifest.version -ne $ExpectedPiVersion) {
        throw 'staged Pi package identity/version does not match the exact request'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installedRoot 'dist\index.js') -PathType Leaf)) {
        throw 'staged Pi package is missing dist/index.js'
    }
}

function Assert-RestoredStateReady {
    $appParent = [IO.Path]::GetDirectoryName((Get-NormalizedPath $AppCurrent))
    Assert-CurrentRoot $AppCurrent $appParent 'restored app root'
    $previousExecutable = Join-Path $AppCurrent $AppExecutableName
    if (-not (Test-Path -LiteralPath $previousExecutable -PathType Leaf)) {
        throw "restored app executable is missing: $previousExecutable"
    }
    Assert-PathAncestorsNotReparse $previousExecutable 'restored app executable'
    if ($ApplyApp) {
        if (Test-Path -LiteralPath $AppBackup) { throw "app backup still exists after rollback: $AppBackup" }
        Assert-TreeNotReparse $AppCurrent 'restored app root'
    }

    if (-not $ApplyPi) { return }
    $managedRoot = Get-NormalizedPath $ManagedRuntimeRoot
    Assert-PathAncestorsNotReparse $managedRoot 'restored managed runtime root'
    if (-not (Test-PathsEqual $PiCurrent (Join-Path $managedRoot 'pi'))) {
        throw "restored Pi root is outside the managed runtime: $PiCurrent"
    }
    if (Test-Path -LiteralPath $PiBackup) { throw "Pi backup still exists after rollback: $PiBackup" }
    Assert-CurrentRoot $PiCurrent $managedRoot 'restored Pi root'
    Assert-TreeNotReparse $PiCurrent 'restored Pi root'

    $markerPath = Join-Path $managedRoot 'ipi.runtime-owner.json'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'restored runtime ownership marker is missing' }
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.owner -ne 'ipi' -or $marker.kind -ne 'managed-runtime' -or -not (Test-PathsEqual ([string]$marker.managedRoot) $managedRoot)) {
        throw 'restored runtime ownership marker is invalid'
    }

    $rootManifestPath = Join-Path $PiCurrent 'package.json'
    $installedRoot = Join-Path $PiCurrent 'node_modules\@earendil-works\pi-coding-agent'
    $installedManifestPath = Join-Path $installedRoot 'package.json'
    if (-not (Test-Path -LiteralPath $rootManifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $installedRoot 'dist\index.js') -PathType Leaf)) {
        throw 'restored Pi runtime is incomplete'
    }
    $rootManifest = Get-Content -LiteralPath $rootManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($rootManifest.name -ne 'ipi-managed-pi-runtime' -or $rootManifest.private -ne $true -or
        $installedManifest.name -ne '@earendil-works/pi-coding-agent' -or
        ([string]$installedManifest.version) -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw 'restored Pi runtime identity is invalid'
    }
}

function Stop-ProcessTree([int]$ProcessId) {
    $taskKill = Join-Path $env:SystemRoot 'System32\taskkill.exe'
    if (Test-Path -LiteralPath $taskKill -PathType Leaf) {
        & $taskKill /PID $ProcessId /T /F 2>&1 | Out-Null
    }
    else {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Move-DirectoryAtomic([string]$Source, [string]$Destination) {
    [IO.Directory]::Move((Get-NormalizedPath $Source), (Get-NormalizedPath $Destination))
}

function Restore-Swap([string]$Current, [string]$Stage, [string]$Backup, [bool]$NewActive, [bool]$OldBackedUp, [string]$Label) {
    if ($NewActive -and (Test-Path -LiteralPath $Current -PathType Container)) {
        if (Test-Path -LiteralPath $Stage) { throw "$Label rollback stage unexpectedly exists: $Stage" }
        Move-DirectoryAtomic $Current $Stage
    }
    if ($OldBackedUp -and (Test-Path -LiteralPath $Backup -PathType Container)) {
        if (Test-Path -LiteralPath $Current) { throw "$Label rollback destination is occupied: $Current" }
        Move-DirectoryAtomic $Backup $Current
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($AppExecutableName) -or
        -not [string]::Equals($AppExecutableName, [IO.Path]::GetFileName($AppExecutableName), [StringComparison]::Ordinal)) {
        throw "AppExecutableName must be a non-empty file name"
    }
    Assert-AppSwapReady
    Assert-PiSwapReady

    Write-UpdateLog "waiting for parent process $ParentProcessId"
    $parentExited = Wait-ForProcessExit $ParentProcessId 60000
    if (-not $parentExited) { throw "parent process did not exit within 60 seconds" }

    # Revalidate at the switch boundary after the writable parent process is gone.
    Assert-AppSwapReady
    Assert-PiSwapReady

    if ($ApplyApp) {
        Write-UpdateLog 'activating staged app directory'
        Move-DirectoryAtomic $AppCurrent $AppBackup
        $appOldBackedUp = $true
        Move-DirectoryAtomic $AppStage $AppCurrent
        $appNewActive = $true
    }

    if ($ApplyPi) {
        Write-UpdateLog 'activating staged Pi directory'
        Move-DirectoryAtomic $PiCurrent $PiBackup
        $piOldBackedUp = $true
        Move-DirectoryAtomic $PiStage $PiCurrent
        $piNewActive = $true
    }

    $activeExecutable = Join-Path $AppCurrent $AppExecutableName
    if (-not (Test-Path -LiteralPath $activeExecutable -PathType Leaf)) {
        throw "active app executable is missing after swap: $activeExecutable"
    }
    Write-UpdateLog 'starting updated ipi'
    $restartedProcess = Start-Process -FilePath $activeExecutable -WorkingDirectory $AppCurrent -PassThru -ErrorAction Stop
    Start-Sleep -Milliseconds 5000
    if ($restartedProcess.HasExited) {
        throw "updated ipi exited immediately with code $($restartedProcess.ExitCode)"
    }

    Write-UpdateLog 'updated ipi started'
    $restartedProcess.Dispose()
    $restartedProcess = $null
}
catch {
    $failure = $_.Exception.Message
    Write-UpdateLog "update transaction failed: $failure"
    if ($null -ne $restartedProcess -and -not $restartedProcess.HasExited) {
        try { Stop-ProcessTree $restartedProcess.Id } catch { }
        try { [void](Wait-ForProcessExit $restartedProcess.Id 10000) } catch { }
    }

    $rollbackFailed = $false
    if ($ApplyPi) {
        try { Restore-Swap $PiCurrent $PiStage $PiBackup $piNewActive $piOldBackedUp 'Pi' }
        catch { $rollbackFailed = $true; Write-UpdateLog "Pi rollback failed: $($_.Exception.Message)" }
    }
    if ($ApplyApp) {
        try { Restore-Swap $AppCurrent $AppStage $AppBackup $appNewActive $appOldBackedUp 'app' }
        catch { $rollbackFailed = $true; Write-UpdateLog "app rollback failed: $($_.Exception.Message)" }
    }
    if (-not $rollbackFailed) {
        try {
            Assert-RestoredStateReady
            Write-UpdateLog 'previous directories restored and revalidated'
        }
        catch {
            $rollbackFailed = $true
            Write-UpdateLog "restored state validation failed: $($_.Exception.Message)"
        }
    }

    if (-not $rollbackFailed) {
        try {
            $remainingParent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
            if ($null -ne $remainingParent) {
                $remainingParent.Dispose()
                if (-not (Wait-ForProcessExit $ParentProcessId 60000)) {
                    throw 'original ipi process is still running; refusing to start a duplicate instance'
                }
            }
            $previousExecutable = Join-Path $AppCurrent $AppExecutableName
            $previousProcess = Start-Process -FilePath $previousExecutable -WorkingDirectory $AppCurrent -PassThru -ErrorAction Stop
            Start-Sleep -Milliseconds 5000
            if ($previousProcess.HasExited) {
                Write-UpdateLog "previous ipi exited during rollback health check with code $($previousProcess.ExitCode)"
            }
            else {
                Write-UpdateLog 'previous ipi restarted after rollback'
            }
            $previousProcess.Dispose()
        }
        catch {
            Write-UpdateLog "previous ipi restart failed: $($_.Exception.Message)"
        }
    }
    else {
        Write-UpdateLog 'rollback was incomplete; ipi will not restart automatically'
    }
    exit 1
}

Write-UpdateLog 'removing update backups'
if ($piOldBackedUp -and (Test-Path -LiteralPath $PiBackup -PathType Container)) {
    try {
        $piParent = [IO.Path]::GetDirectoryName((Get-NormalizedPath $PiCurrent))
        Assert-SwapPath $PiBackup $piParent '.ipi-pi-backup-' $true 'Pi backup cleanup'
        Assert-TreeNotReparse $PiBackup 'Pi backup cleanup'
        Remove-Item -LiteralPath $PiBackup -Recurse -Force
    }
    catch { Write-UpdateLog "Pi backup cleanup failed: $($_.Exception.Message)" }
}
if ($appOldBackedUp -and (Test-Path -LiteralPath $AppBackup -PathType Container)) {
    try {
        $appParent = [IO.Path]::GetDirectoryName((Get-NormalizedPath $AppCurrent))
        Assert-SwapPath $AppBackup $appParent '.ipi-app-backup-' $true 'app backup cleanup'
        Assert-TreeNotReparse $AppBackup 'app backup cleanup'
        Remove-Item -LiteralPath $AppBackup -Recurse -Force
    }
    catch { Write-UpdateLog "app backup cleanup failed: $($_.Exception.Message)" }
}
Write-UpdateLog 'update transaction completed'
exit 0
