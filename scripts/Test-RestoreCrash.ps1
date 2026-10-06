param([string]$Executable, [switch]$Smoke)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64\app\CommuteCast.Maintenance.exe' }
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Publish the portable application first, or supply -Executable.' }
& dotnet build (Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Acceptance fixture host build failed.' }
$fixtureHost = Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures\bin\Release\net10.0\CommuteCast.AcceptanceFixtures.exe'
# The unshipped fixture host independently fences all writes to this repository's isolated artifact roots.
$fixture = Join-Path $projectRoot ('artifacts\installation-acceptance\restore-crash-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Invoke-JsonTool([string]$Tool, [string[]]$Arguments, [int]$ExpectedExit = 0) {
    $response = & $Tool @Arguments 2>&1
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Fixture command exit $LASTEXITCODE, expected $ExpectedExit. $response" }
    if ($ExpectedExit -eq 0) { return (($response -join "`n") | ConvertFrom-Json) }
    return ($response -join "`n")
}
function Same($Actual, $Expected, [string]$Label) { if ($Actual -ne $Expected) { throw "$Label did not match." } }
function Canonical($Value) { return ($Value | ConvertTo-Json -Depth 12 -Compress) }
function Managed-Inventory([string]$Root) {
    $files = [Collections.Generic.List[string]]::new()
    foreach ($name in @('queue.db','queue.db-wal','queue.db-shm','queue.db-journal','settings.json','draft.json','provider-lock.local.json','recovery-kokoro.json','recovery-piper.json')) {
        $path = Join-Path $Root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add($path) }
    }
    $jobs = Join-Path $Root 'jobs'
    if (Test-Path -LiteralPath $jobs) { Get-ChildItem -LiteralPath $jobs -File -Recurse | ForEach-Object { $files.Add($_.FullName) } }
    return @($files | Sort-Object | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_).Replace('\','/'); bytes=(Get-Item -LiteralPath $_).Length; sha256=(Get-FileHash -LiteralPath $_).Hash} })
}
function Backup-Inventory([string]$Backup) {
    $manifest = Get-Content -LiteralPath (Join-Path $Backup 'manifest.json') -Raw | ConvertFrom-Json
    return @($manifest.Files | Sort-Object RelativePath | ForEach-Object { [ordered]@{path=$_.RelativePath;bytes=$_.Bytes;sha256=$_.Sha256} })
}
function Protected-Inventory([string]$Root, [string]$Case) {
    $files = @((Join-Path $Root 'keep-unrelated.txt'),(Join-Path $Case 'separate-exports\keep-export.txt'))
    $files += @(Get-ChildItem -LiteralPath (Join-Path $Root 'backups') -File -Recurse | Select-Object -ExpandProperty FullName)
    return @($files | Sort-Object | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($Case,$_).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_).Hash} })
}
function Kill-AtBoundary([string]$Root, [string[]]$Arguments, [string]$Checkpoint, [string]$Item, [string]$ExpectedPhase, [string]$EvidenceName) {
    $case = Split-Path -Parent $Root; $marker = Join-Path $case 'boundary.json'; $journal = Join-Path $Root 'recovery\restore.pending.json'
    $start = [Diagnostics.ProcessStartInfo]::new($fixtureHost)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    $output = $child.StandardOutput.ReadToEndAsync(); $errors = $child.StandardError.ReadToEndAsync()
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew(); $observed = $null
        while ($watch.Elapsed.TotalSeconds -lt 60) {
            if ($child.HasExited) { throw "Owned child exited before the boundary: $($errors.GetAwaiter().GetResult())" }
            if (Test-Path -LiteralPath $marker -PathType Leaf) {
                try { $observed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json } catch { $observed = $null }
                if ($observed) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $observed) { throw 'No live, durable boundary signal arrived within 60 seconds.' }
        Same $observed.processId $child.Id 'Exact live child identity'
        Same $observed.command $Arguments[0] 'Exact child operation'
        Same $observed.root $Root 'Exact isolated workspace'
        Same $observed.checkpoint $Checkpoint 'Observed boundary'
        Same ([string]$observed.item) ([string]$Item) 'Observed boundary item'
        Same (Get-FileHash -LiteralPath $journal).Hash $observed.journalHash 'Durable restore journal hash'
        $journalRecord = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
        Same $journalRecord.Phase $ExpectedPhase 'Durable restore phase'
        $refused = Invoke-JsonTool $Executable @('recover','--root',$Root) 1
        if ($refused -notmatch 'already holds') { throw 'Concurrent recovery did not refuse the live child workspace lease.' }
        if ($child.HasExited) { throw 'The child was no longer live at the process-kill boundary.' }
        $child.Kill($true)
        if (-not $child.WaitForExit(10000)) { throw 'The owned fixture child did not terminate within ten seconds.' }
        if ($child.ExitCode -eq 0) { throw 'A successful normal child exit is not process-kill evidence.' }
        $childOutput = $output.GetAwaiter().GetResult(); $childErrors = $errors.GetAwaiter().GetResult()
        Move-Item -LiteralPath $marker -Destination (Join-Path $case $EvidenceName)
        return [ordered]@{processId=$observed.processId;checkpoint=$Checkpoint;item=$Item;phase=$journalRecord.Phase;journalHash=$observed.journalHash;observedLive=$true;concurrentRecoveryRefused=$true;terminatedThroughOwnedHandle=$true;exitCode=$child.ExitCode;retainedPrevious=(Join-Path $Root ('recovery\restores\' + $journalRecord.Id + '\previous'))}
    } finally {
        # Never stop a process found by name/PID search: this is only our retained child handle.
        if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit(10000) | Out-Null }
        $child.Dispose()
    }
}
$scenarios = [Collections.Generic.List[object]]::new()
$scenarios.Add([pscustomobject]@{checkpoint='Prepared';item=$null;phase='Prepared';recovery=$null;recoveryItem=$null})
foreach ($item in @('queue.db','settings.json','draft.json','provider-lock.local.json','recovery-piper.json','jobs')) {
    $scenarios.Add([pscustomobject]@{checkpoint='OldItemMoved';item=$item;phase='Prepared';recovery=$null;recoveryItem=$null})
}
$scenarios.Add([pscustomobject]@{checkpoint='OldMoved';item=$null;phase='OldMoved';recovery=$null;recoveryItem=$null})
foreach ($item in @('queue.db','settings.json','draft.json','provider-lock.local.json','recovery-piper.json','jobs')) {
    $scenarios.Add([pscustomobject]@{checkpoint='NewItemMoved';item=$item;phase='OldMoved';recovery=$null;recoveryItem=$null})
}
$scenarios.Add([pscustomobject]@{checkpoint='NewMoved';item=$null;phase='OldMoved';recovery=$null;recoveryItem=$null})
$scenarios.Add([pscustomobject]@{checkpoint='Committed';item=$null;phase='Committed';recovery=$null;recoveryItem=$null})
foreach ($point in @('IncomingRetained','OriginalRestored')) {
    foreach ($item in @('queue.db','settings.json','jobs')) {
        $scenarios.Add([pscustomobject]@{checkpoint='NewMoved';item=$null;phase='OldMoved';recovery=$point;recoveryItem=$item})
    }
}
$scenarios.Add([pscustomobject]@{checkpoint='Committed';item=$null;phase='Committed';recovery='CommittedStateRetained';recoveryItem=$null})
if ($Smoke) { $scenarios = @($scenarios[0],$scenarios[14],$scenarios[15],$scenarios[16],$scenarios[21],$scenarios[22]) }
$results = [Collections.Generic.List[object]]::new(); $number = 0
foreach ($scenario in $scenarios) {
    $number++; $case = Join-Path $fixture ('case-' + $number.ToString('D2')); $root = Join-Path $case 'private'
    $original = Invoke-JsonTool $fixtureHost @('seed-audio',$root,'original')
    $backup = (Invoke-JsonTool $Executable @('backup','--root',$root)).backup
    $incomingFiles = Backup-Inventory $backup
    $later = Invoke-JsonTool $fixtureHost @('seed-audio',$root,'later')
    Set-Content -LiteralPath (Join-Path $root 'keep-unrelated.txt') -Value 'Unrelated private sentinel'
    Set-Content -LiteralPath (Join-Path $case 'separate-exports\keep-export.txt') -Value 'Separate export sentinel'
    $laterFiles = Managed-Inventory $root; $protected = Protected-Inventory $root $case
    $restoreArgs = @('restore-barrier',$root,$backup,$scenario.checkpoint)
    if ($scenario.item) { $restoreArgs += $scenario.item }
    $stop = Kill-AtBoundary $root $restoreArgs $scenario.checkpoint $scenario.item $scenario.phase 'observed-restore-boundary.json'
    $recoveryStop = $null
    if ($scenario.recovery) {
        $recoveryArgs = @('recover-barrier',$root,$scenario.recovery)
        if ($scenario.recoveryItem) { $recoveryArgs += $scenario.recoveryItem }
        $recoveryStop = Kill-AtBoundary $root $recoveryArgs $scenario.recovery $scenario.recoveryItem $scenario.phase 'observed-recovery-boundary.json'
    }
    $recovered = Invoke-JsonTool $Executable @('recover','--root',$root)
    Same $recovered.recoveredInterruptedRestore $true 'Recovery after actual process termination'
    Same (Invoke-JsonTool $Executable @('recover','--root',$root)).recoveredInterruptedRestore $false 'Idempotent settled recovery'
    if (Test-Path -LiteralPath (Join-Path $root 'recovery\restore.pending.json')) { throw 'A settled restore journal remains.' }
    $committed = $scenario.phase -eq 'Committed'
    $expectedFiles = if ($committed) { $incomingFiles } else { $laterFiles }
    Same (Canonical (Managed-Inventory $root)) (Canonical $expectedFiles) 'Every managed path, byte count and SHA256 after recovery'
    if ($committed) { Same (Canonical (Managed-Inventory $stop.retainedPrevious)) (Canonical $laterFiles) 'Every previous-state path and byte after committed restore' }
    Same (Canonical (Protected-Inventory $root $case)) (Canonical $protected) 'Backups, unrelated local data and separate exports'
    $actual = Invoke-JsonTool $fixtureHost @('inspect',$root)
    $expected = if ($committed) { $original } else { $later }
    Same (Canonical $actual) (Canonical $expected) 'Frozen job/source/settings/time/receipt/MP3 state after reopening SQLite'
    if (@($actual.jobs | Where-Object { -not $_.finalHash }).Count -gt 0) { throw 'Fixture did not contain real validated MP3 bytes.' }
    $result = [ordered]@{case=$case;restore=$stop;recovery=$recoveryStop;recoveredState=$(if ($committed) {'committed backup'} else {'exact pre-restore state'});managedFilesCompared=@($expectedFiles).Count;realPcmAndMp3Preserved=$true;frozenJobsPreserved=$true;previousStatePreserved=$committed;unrelatedAndBackupFilesPreserved=$true;recoveryIdempotent=$true}
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $case 'report.json') -Encoding utf8
    $results.Add($result)
    Write-Host "Passed $number/$($scenarios.Count): $($scenario.checkpoint) $($scenario.item) $($scenario.recovery) $($scenario.recoveryItem)"
}
$evidence = [ordered]@{passed=$true;smoke=[bool]$Smoke;fixture=$fixture;executable=$Executable;fixtureHost=$fixtureHost;fixtureBuild=(Get-Item $fixtureHost).VersionInfo.ProductVersion;maintenanceBuild=(Get-Item $Executable).VersionInfo.ProductVersion;repositoryCommit=(git -C $projectRoot rev-parse HEAD);repositoryDirty=(-not [string]::IsNullOrWhiteSpace((git -C $projectRoot status --porcelain | Out-String)));cases=$results.Count;processTerminations=($results.Count + @($results | Where-Object {$_.recovery}).Count);results=$results;limits='Isolated Windows process termination at observed durable boundaries; synthetic tone audio, no native UI, power-loss, sleep/wake, Docker inference or phone acceptance';createdUtc=[datetime]::UtcNow.ToString('O')}
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
([pscustomobject]$evidence) | Select-Object passed,smoke,fixture,cases,processTerminations,fixtureBuild,maintenanceBuild,repositoryCommit,repositoryDirty,limits | ConvertTo-Json -Depth 4
