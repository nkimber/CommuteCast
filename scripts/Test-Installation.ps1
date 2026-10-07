param([string]$Executable, [switch]$KeepInstalled, [string]$LegacyPackage,
    [ValidateSet('Prepared','StateMigrated','BeforeActivation','Activated','MigrationBeforeCommit')][string]$ActivationCrashCheckpoint,
    [switch]$LegacySchemaThree, [switch]$RollbackCrashAfterRestore)
$ErrorActionPreference = 'Stop'
if ($LegacySchemaThree -and -not $ActivationCrashCheckpoint) { throw 'Legacy schema interruption requires an activation checkpoint.' }
if ($ActivationCrashCheckpoint -eq 'MigrationBeforeCommit' -and -not $LegacySchemaThree) { throw 'Migration transaction interruption requires LegacySchemaThree.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64\app\CommuteCast.Maintenance.exe' }
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Publish the portable application first.' }
& dotnet build (Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Acceptance fixture host build failed.' }
$fixtureHost = Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures\bin\Release\net10.0\CommuteCast.AcceptanceFixtures.exe'
$fixture = Join-Path $projectRoot ('artifacts\installation-acceptance\' + [guid]::NewGuid().ToString('N'))
$privateRoot = Join-Path $fixture 'private'; $installRoot = Join-Path $fixture 'program'
$portable = Split-Path -Parent (Split-Path -Parent $Executable)
$a = Join-Path $fixture 'source-a'; $b = Join-Path $fixture 'source-b'
New-Item -ItemType Directory -Path $fixture | Out-Null
if ($LegacyPackage) {
    $LegacyPackage = [IO.Path]::GetFullPath($LegacyPackage)
    $legacyCheck = & $Executable verify-package --package $LegacyPackage 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The requested older fixture package is not verified.' }
}
Copy-Item -LiteralPath $(if ($LegacyPackage) { $LegacyPackage } else { $portable }) -Destination $a -Recurse
Copy-Item -LiteralPath $portable -Destination $b -Recurse
# A deliberate package revision, explicitly sealed as a developer fixture.
Add-Content -LiteralPath (Join-Path $b 'README.md') -Value "`nSynthetic installation acceptance revision B."
function Invoke-Tool([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $response = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Maintenance exit $LASTEXITCODE, expected $ExpectedExit. $response" }
    if ($ExpectedExit -eq 0) { return (($response -join "`n") | ConvertFrom-Json) }
    return ($response -join "`n")
}
function Fixture-State([string]$Command, [string]$Label) {
    $response = if ($Label) { & $fixtureHost $Command $privateRoot $Label } else { & $fixtureHost $Command $privateRoot }
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture operation failed.' }
    return (($response -join "`n") | ConvertFrom-Json)
}
function Same($Actual, $Expected, [string]$Label) { if ($Actual -ne $Expected) { throw "$Label did not match." } }
$hasLauncher = Test-Path -LiteralPath (Join-Path $portable 'app\CommuteCast.Launcher.exe')
$hasWindowsIntegration = Test-Path -LiteralPath (Join-Path $portable 'app\windows-integration.json')
$hasSetupCacheCleanup = ((& $Executable --help) -join "`n").Contains('review-setup-cache')
$cacheCleanupEvidence = $null
$launcherPlanTimings = [Collections.Generic.List[object]]::new()
function Verify-WindowsIntegration([bool]$Present) {
    if (-not $hasWindowsIntegration) { return }
    $owner = Get-Content -LiteralPath (Join-Path $installRoot 'installation.owner.json') -Raw | ConvertFrom-Json
    $registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CommuteCast-' + $owner.InstallationId
    $shortcutRoot = Join-Path ([Environment]::GetFolderPath('Programs')) ('CommuteCast-' + $owner.InstallationId)
    if ($Present) {
        $entry = Get-ItemProperty -LiteralPath $registryPath
        Same $entry.CommuteCastOwner $owner.InstallationId 'Windows registration owner'
        Same $entry.InstallLocation $installRoot 'Windows registered root'
        Same $entry.UninstallString ('"' + (Join-Path $installRoot 'CommuteCast.exe') + '" --setup') 'Reviewed Windows removal entry'
        foreach ($name in @('CommuteCast.lnk','CommuteCast setup.lnk')) { if (-not (Test-Path -LiteralPath (Join-Path $shortcutRoot $name) -PathType Leaf)) { throw 'An owned Start Menu shortcut is missing.' } }
    } else {
        if (Test-Path -LiteralPath $registryPath) { throw 'Owned Installed Apps entry survived uninstall.' }
        foreach ($name in @('CommuteCast.lnk','CommuteCast setup.lnk')) { if (Test-Path -LiteralPath (Join-Path $shortcutRoot $name)) { throw 'An owned Start Menu shortcut survived uninstall.' } }
    }
}
function Launcher-Plan([switch]$Setup) {
    # First setup copies, flushes and verifies the complete self-contained distribution.
    # An observed 430-file copy took 37.5 seconds; ordinary read-only plans keep 30 seconds.
    $allowanceMs = if ($Setup) { 120000 } else { 30000 }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $installRoot 'CommuteCast.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--inspect'); if ($Setup) { $start.ArgumentList.Add('--setup') }
    # Inspect the actual host: static CLR, or an extracted CLR under controlled bundle storage.
    $start.Environment['DOTNET_ROOT'] = Join-Path $fixture 'absent-runtime'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $fixture 'absent-runtime'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $fixture 'launcher-runtime'
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit($allowanceMs)) { $process.Kill($true); throw "Owned launcher inspection exceeded its $($allowanceMs / 1000)-second allowance (setup=$([bool]$Setup))." }
        $output = $stdout.GetAwaiter().GetResult(); $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Launcher inspection failed: $errors" }
        $plan = $output | ConvertFrom-Json
        if ($plan.bundledRuntimeLoaded -ne $true) { throw 'Launcher did not report a statically linked or isolated extracted CLR.' }
        $launcherPlanTimings.Add([ordered]@{setup=[bool]$Setup;wallSeconds=$watch.Elapsed.TotalSeconds;allowanceSeconds=$allowanceMs / 1000;packageId=$plan.PackageId})
        return $plan
    } finally { $process.Dispose() }
}
$sealed = Invoke-Tool -Arguments @('seal-package', '--package', $b)
$original = Fixture-State seed original
$held = [IO.FileStream]::new((Join-Path $privateRoot 'instance.lease'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $refused = Invoke-Tool -Arguments @('install', '--root', $privateRoot, '--install-root', $installRoot, '--package', $a) -ExpectedExit 1 }
finally { $held.Dispose() }
if ($refused -notmatch 'already holds') { throw 'Running workspace did not exclude installation.' }
$first = Invoke-Tool -Arguments @('install', '--root', $privateRoot, '--install-root', $installRoot, '--package', $a)
Verify-WindowsIntegration $true
if ($hasLauncher) { Same (Launcher-Plan).Executable $first.Executable 'Stable launcher initial release' }
$firstExternalSetup = if ($hasLauncher -and $hasSetupCacheCleanup) { Launcher-Plan -Setup } else { $null }
$activationCrashEvidence = $null
if ($ActivationCrashCheckpoint) {
    $beforeCrashState = Fixture-State inspect
    if ($LegacySchemaThree) { Same (Fixture-State legacy-schema-three).version 3 'Initial legacy schema' }
    $marker = Join-Path $fixture 'boundary.json'
    $start = [Diagnostics.ProcessStartInfo]::new($fixtureHost)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('activation-barrier',$privateRoot,$b,$ActivationCrashCheckpoint)) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    $childOutput = $child.StandardOutput.ReadToEndAsync(); $childErrors = $child.StandardError.ReadToEndAsync()
    try {
        $deadline = [datetime]::UtcNow.AddSeconds(180)
        while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            if ($child.HasExited) { throw ('Activation host exited early: ' + $childErrors.GetAwaiter().GetResult()) }
            if ([datetime]::UtcNow -gt $deadline) { throw 'Activation checkpoint was not observed within 180 seconds.' }
            Start-Sleep -Milliseconds 50
        }
        $observed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($observed.processId -ne $child.Id -or $child.HasExited -or $observed.point -ne $ActivationCrashCheckpoint -or $observed.PackageId -ne $sealed.PackageId) { throw 'Activation marker does not identify the live owned process and revision.' }
        Same $observed.schemaVersion $(if ($LegacySchemaThree -and $ActivationCrashCheckpoint -in @('Prepared','MigrationBeforeCommit')) { 3 } else { 4 }) 'Committed schema at host-loss boundary'
        if ($ActivationCrashCheckpoint -eq 'MigrationBeforeCommit') {
            Same $observed.migrationFrom 3 'Migration transaction source'; Same $observed.migrationTo 4 'Migration transaction target'
        }
        $child.Kill($false)
        if (-not $child.WaitForExit(10000)) { throw 'Owned activation host did not terminate.' }
        $null = $childOutput.GetAwaiter().GetResult(); $null = $childErrors.GetAwaiter().GetResult()
    } finally {
        if (-not $child.HasExited) { $child.Kill($false); $child.WaitForExit(10000) | Out-Null }
        $child.Dispose()
    }
    $recovery = Invoke-Tool -Arguments @('recover-install','--root',$privateRoot,'--install-root',$installRoot)
    Same $recovery.recovered $true 'Pending activation recovered'
    $recoveredSchema = (Fixture-State inspect-schema).version
    Same $recoveredSchema $(if ($LegacySchemaThree -and $ActivationCrashCheckpoint -ne 'Activated') { 3 } else { 4 }) 'Schema before ordinary queue reopening'
    $afterCrash = Invoke-Tool -Arguments @('inspect-install','--root',$privateRoot,'--install-root',$installRoot)
    Same $afterCrash.State.CurrentPackageId $(if ($ActivationCrashCheckpoint -eq 'Activated') { $sealed.PackageId } else { $first.State.CurrentPackageId }) 'Recovered activation side of commit'
    Same ((Fixture-State inspect) | ConvertTo-Json -Depth 8 -Compress) ($beforeCrashState | ConvertTo-Json -Depth 8 -Compress) 'Activation host loss preserves frozen private state'
    if (Test-Path -LiteralPath (Join-Path $installRoot 'deployment.pending.json')) { throw 'Activation recovery retained pending intent.' }
    Verify-WindowsIntegration $true
    $activationCrashEvidence = [ordered]@{passed=$true;point=$ActivationCrashCheckpoint;parentOnlyLoss=$true;boundary=$observed;recoveredPackage=$afterCrash.State.CurrentPackageId;recoveredSchema=$recoveredSchema;legacySchemaThree=[bool]$LegacySchemaThree;frozenStatePreserved=$true}
}
$second = Invoke-Tool -Arguments @('install', '--install-root', $installRoot, '--package', $b)
if ($hasLauncher) { Same (Launcher-Plan).Executable $second.Executable 'Stable launcher updated release' }
Same $second.State.Previous.PackageId $first.State.CurrentPackageId 'Previous binary identity'
$later = Fixture-State seed later
$beforePointer = (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash
$unconfirmed = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot) -ExpectedExit 1
if ($unconfirmed -notmatch 'requires.*confirm-replace') { throw 'Rollback did not require explicit replacement intent.' }
Same (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash $beforePointer 'Unconfirmed rollback preserves activation'
$rollbackCrashEvidence = $null
if ($RollbackCrashAfterRestore) {
    $beforeRollback = Fixture-State inspect
    $rollbackMarker = Join-Path $fixture 'rollback-boundary.json'
    $rollbackStart = [Diagnostics.ProcessStartInfo]::new($fixtureHost)
    $rollbackStart.UseShellExecute = $false; $rollbackStart.CreateNoWindow = $true; $rollbackStart.WindowStyle = 'Hidden'
    $rollbackStart.RedirectStandardOutput = $true; $rollbackStart.RedirectStandardError = $true
    foreach ($argument in @('rollback-barrier',$privateRoot)) { $rollbackStart.ArgumentList.Add($argument) }
    $rollbackHost = [Diagnostics.Process]::Start($rollbackStart)
    $rollbackOutput = $rollbackHost.StandardOutput.ReadToEndAsync(); $rollbackErrors = $rollbackHost.StandardError.ReadToEndAsync()
    try {
        $deadline = [datetime]::UtcNow.AddSeconds(180)
        while (-not (Test-Path -LiteralPath $rollbackMarker -PathType Leaf)) {
            if ($rollbackHost.HasExited) { throw ('Rollback host exited early: ' + $rollbackErrors.GetAwaiter().GetResult()) }
            if ([datetime]::UtcNow -gt $deadline) { throw 'Rollback boundary was not observed within 180 seconds.' }
            Start-Sleep -Milliseconds 50
        }
        $rollbackObserved = Get-Content -LiteralPath $rollbackMarker -Raw | ConvertFrom-Json
        if ($rollbackObserved.processId -ne $rollbackHost.Id -or $rollbackHost.HasExited -or $rollbackObserved.point -ne 'StateRestored' -or $rollbackObserved.restoredJobs -ne 1) { throw 'Rollback marker does not identify the live owned host and restored fixture.' }
        $rollbackHost.Kill($false)
        if (-not $rollbackHost.WaitForExit(10000)) { throw 'Owned rollback host did not terminate.' }
        $null = $rollbackOutput.GetAwaiter().GetResult(); $null = $rollbackErrors.GetAwaiter().GetResult()
    } finally {
        if (-not $rollbackHost.HasExited) { $rollbackHost.Kill($false); $rollbackHost.WaitForExit(10000) | Out-Null }
        $rollbackHost.Dispose()
    }
    Same (Invoke-Tool -Arguments @('recover-install','--root',$privateRoot,'--install-root',$installRoot)).recovered $true 'Interrupted rollback recovered'
    Same (Invoke-Tool -Arguments @('inspect-install','--root',$privateRoot,'--install-root',$installRoot)).State.CurrentPackageId $second.State.CurrentPackageId 'Uncommitted rollback retains current package'
    Same ((Fixture-State inspect) | ConvertTo-Json -Depth 8 -Compress) ($beforeRollback | ConvertTo-Json -Depth 8 -Compress) 'Rollback recovery restores exact later private state'
    if (Test-Path -LiteralPath (Join-Path $installRoot 'deployment.pending.json')) { throw 'Rollback recovery retained pending intent.' }
    Verify-WindowsIntegration $true
    $rollbackCrashEvidence = [ordered]@{passed=$true;parentOnlyLoss=$true;boundary=$rollbackObserved;laterStatePreserved=$true}
}
$rolledBack = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot, '--confirm-replace-local-data')
Same $rolledBack.State.CurrentPackageId $first.State.CurrentPackageId 'Rollback binary identity'
if ($hasLauncher) { Same (Launcher-Plan).Executable $rolledBack.Executable 'Stable launcher rolled-back release' }
$restored = Fixture-State inspect
Same $restored.jobs.Count 1 'Pre-update queued job count'
Same $restored.jobs[0].immutablePayloadHash $original.jobs[0].immutablePayloadHash 'Frozen queued job payload'
Same $restored.jobs[0].chunks[0].hash $original.jobs[0].chunks[0].hash 'Original checked PCM bytes'
foreach ($field in @('draftHash', 'settingsHash', 'providerPinHash')) { Same $restored.$field $original.$field $field }
$undone = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot, '--confirm-replace-local-data')
Verify-WindowsIntegration $true
Same $undone.State.CurrentPackageId $second.State.CurrentPackageId 'Undo rollback binary identity'
Same ((Fixture-State inspect).jobs.Count) 2 'Later queued jobs retained by undo snapshot'
$externalSetup = $null
if ($hasLauncher) {
    Same (Launcher-Plan).Executable $undone.Executable 'Stable launcher undo release'
    $externalSetup = Launcher-Plan -Setup
    if ($externalSetup.Executable.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Setup was staged inside its removal scope.' }
    $externalPackage = Split-Path -Parent (Split-Path -Parent $externalSetup.Executable)
    $verifiedSetup = Invoke-Tool -Arguments @('verify-package', '--package', $externalPackage)
    Same $verifiedSetup.PackageId $second.State.CurrentPackageId 'External setup package identity'
}
if ($firstExternalSetup -and $hasSetupCacheCleanup) {
    $oldCopy = Split-Path -Parent (Split-Path -Parent $firstExternalSetup.Executable)
    $cacheReview = Invoke-Tool -Arguments @('review-setup-cache','--install-root',$installRoot)
    Same $cacheReview.Copies 1 'One older setup distribution is eligible'
    # Run a real older/current cached maintenance process read-only. Residency/usage must retain it.
    $probeStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $oldCopy 'app\CommuteCast.Maintenance.exe'))
    $probeStart.UseShellExecute = $false; $probeStart.CreateNoWindow = $true; $probeStart.WindowStyle = 'Hidden'
    $probeStart.RedirectStandardOutput = $true; $probeStart.RedirectStandardError = $true
    foreach ($argument in @('check-setup','--root',(Join-Path $fixture 'absent-cache-probe-private'))) { $probeStart.ArgumentList.Add($argument) }
    $probe = [Diagnostics.Process]::Start($probeStart); $probeOutput = $probe.StandardOutput.ReadToEndAsync(); $probeErrors = $probe.StandardError.ReadToEndAsync()
    try {
        if ($probe.HasExited) { throw 'Cached-process probe exited before residency observation.' }
        $busyReview = Invoke-Tool -Arguments @('review-setup-cache','--install-root',$installRoot)
        Same $busyReview.Copies 0 'Running cached maintenance is preserved'
        if ($probe.HasExited -or -not ($busyReview.Preserved | Where-Object { $_.Name -eq $first.State.CurrentPackageId -and $_.Reason -match 'in use|running|residency' })) { throw 'No live cached-process preservation evidence was observed.' }
        if (-not $probe.WaitForExit(45000)) { $probe.Kill($true); throw 'Owned read-only cached-process probe timed out.' }
        $probeText = $probeOutput.GetAwaiter().GetResult(); $probeErrorText = $probeErrors.GetAwaiter().GetResult()
        if ($probe.ExitCode -ne 0) { throw "Cached-process probe failed: $probeErrorText" }
    } finally { if (-not $probe.HasExited) { $probe.Kill($true); $probe.WaitForExit() }; $probe.Dispose() }
    $cacheReview = Invoke-Tool -Arguments @('review-setup-cache','--install-root',$installRoot)
    Same $cacheReview.Copies 1 'Closed cached process makes the older copy eligible'
    $scopeRefused = Invoke-Tool -Arguments @('clean-setup-cache','--install-root',$installRoot,'--confirm-remove-setup-copies','--review-fingerprint',('0' * 64)) -ExpectedExit 1
    if ($scopeRefused -notmatch 'scope changed') { throw 'Changed cache review fingerprint was not refused.' }
    $unconfirmedCache = Invoke-Tool -Arguments @('clean-setup-cache','--install-root',$installRoot,'--review-fingerprint',$cacheReview.Fingerprint) -ExpectedExit 1
    if ($unconfirmedCache -notmatch 'confirm-remove-setup-copies') { throw 'Cache removal did not require explicit confirmation.' }
    $privateBeforeCacheCleanup = Fixture-State inspect
    $cacheCleaned = Invoke-Tool -Arguments @('clean-setup-cache','--install-root',$installRoot,'--confirm-remove-setup-copies','--review-fingerprint',$cacheReview.Fingerprint)
    Same $cacheCleaned.removed.Copies 1 'Reviewed unused setup copy removal'
    if (Test-Path -LiteralPath $oldCopy) { throw 'The unused owned setup distribution survived cleanup.' }
    if (-not (Test-Path -LiteralPath $externalSetup.Executable)) { throw 'The active setup kit was removed.' }
    Same ((Fixture-State inspect) | ConvertTo-Json -Depth 8 -Compress) ($privateBeforeCacheCleanup | ConvertTo-Json -Depth 8 -Compress) 'Cache cleanup preserves private narration state'
    $cacheCleanupEvidence = [ordered]@{passed=$true;liveCachedProcessPreserved=$true;legacyPackage=$LegacyPackage;copiesRemoved=$cacheCleaned.removed.Copies;bytesReclaimed=$cacheCleaned.removed.Bytes;privateStateUnchanged=$true;requiredKitPreserved=$true}
}
$export = Join-Path $fixture 'separate-exports\keep-export.txt'; Set-Content -LiteralPath $export -Value 'Separate export sentinel'
$note = Join-Path $privateRoot 'keep-unrelated.txt'; Set-Content -LiteralPath $note -Value 'Unrelated local sentinel'
$model = Join-Path $privateRoot 'provisioning-models\keep.txt'; New-Item -ItemType Directory -Path (Split-Path -Parent $model) | Out-Null; Set-Content -LiteralPath $model -Value 'Separate model sentinel'
if (-not $KeepInstalled) {
    $preview = Fixture-State seed-preview-removal
    $previewBefore = Fixture-State inspect
    $pointerBeforePreview = (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash
    $previewRefused = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'remove', '--confirm-remove-local-data') -ExpectedExit 1
    if ($previewRefused -notmatch 'previews must be stopped or reconciled') { throw 'Unresolved preview ownership did not block packaged private removal.' }
    Same (Get-FileHash -LiteralPath $preview.path).Hash $preview.hash 'Uninstall refusal preserves original preview bytes'
    Same (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash $pointerBeforePreview 'Preview refusal preserves activation'
    if (Test-Path -LiteralPath (Join-Path $installRoot 'deployment.pending.json')) { throw 'Preview refusal wrote uninstall intent.' }
    Same ((Fixture-State inspect) | ConvertTo-Json -Depth 8 -Compress) ($previewBefore | ConvertTo-Json -Depth 8 -Compress) 'Preview refusal preserves frozen narration state'
    Verify-WindowsIntegration $true
    $previewRecovery = Fixture-State recover-preview-removal
    Same $previewRecovery.reconciled 1 'Original-workspace preview reconciliation'
    if (Test-Path -LiteralPath $preview.path) { throw 'Reconciled original preview survived.' }
    $retained = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'retain')
    Verify-WindowsIntegration $false
    if (Test-Path -LiteralPath $undone.Executable) { throw 'Tracked application binary survived uninstall.' }
    if ($hasLauncher -and (Test-Path -LiteralPath (Join-Path $installRoot 'CommuteCast.exe'))) { throw 'Owned stable launcher survived uninstall.' }
    if ($externalSetup -and -not (Test-Path -LiteralPath $externalSetup.Executable)) { throw 'External setup was removed while required for recovery.' }
    Same ((Fixture-State inspect).jobs.Count) 2 'Retain-data uninstall keeps queued jobs'
    $reinstalled = Invoke-Tool -Arguments @('install', '--install-root', $installRoot, '--package', $a)
    Verify-WindowsIntegration $true
    if ($hasLauncher) { Same (Launcher-Plan).Executable $reinstalled.Executable 'Stable launcher reinstallation' }
    $removeRefused = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'remove') -ExpectedExit 1
    if ($removeRefused -notmatch 'requires.*confirm-remove') { throw 'Private data removal was not explicitly gated.' }
    $removed = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'remove', '--confirm-remove-local-data')
    Verify-WindowsIntegration $false
    foreach ($name in @('queue.db', 'jobs', 'draft.json', 'settings.json', 'backups', 'recovery')) { if (Test-Path -LiteralPath (Join-Path $privateRoot $name)) { throw "Managed private scope $name survived requested removal." } }
    Same (Get-Content -LiteralPath $export -Raw).Trim() 'Separate export sentinel' 'Export preservation'
    Same (Get-Content -LiteralPath $note -Raw).Trim() 'Unrelated local sentinel' 'Unrelated-root preservation'
    Same (Get-Content -LiteralPath $model -Raw).Trim() 'Separate model sentinel' 'Model preservation'
}
$report = [ordered]@{ passed = $true; fixture = $fixture; privateRoot = $privateRoot; installRoot = $installRoot; executable = $undone.Executable; keptInstalledForNativeCheck = [bool]$KeepInstalled; checks = @('real-process workspace exclusion', 'versioned initial install and update', 'queued source/settings/time/receipt/artifact preservation', 'explicit compatible state-and-binary rollback', 'undo snapshot retains later jobs'); uninstallScopesExecuted = (-not $KeepInstalled); sourcePackage = $portable; createdUtc = [datetime]::UtcNow.ToString('O') }
$report['launcherPlansExecuted'] = $hasLauncher
$report['launcherPlanTimings'] = $launcherPlanTimings.ToArray()
$report['bundledRuntimeInspected'] = $hasLauncher
$report['externalSetup'] = $externalSetup
$report['windowsIntegrationInspected'] = $hasWindowsIntegration
$report['previewRemovalGuardExecuted'] = (-not $KeepInstalled)
$report['activationHostLoss'] = $activationCrashEvidence
$report['rollbackHostLoss'] = $rollbackCrashEvidence
$report['setupCacheCleanup'] = $cacheCleanupEvidence
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
