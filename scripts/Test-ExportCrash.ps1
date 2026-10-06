param([string]$Executable, [switch]$Smoke)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'tools\CommuteCast.Maintenance\bin\Release\net10.0\CommuteCast.Maintenance.exe' }
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Build maintenance or supply its executable for lease exclusion checks.' }
& dotnet build (Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Export fixture build failed.' }
$fixtureHost = Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures\bin\Release\net10.0\CommuteCast.AcceptanceFixtures.exe'
$fixture = Join-Path $projectRoot ('artifacts\installation-acceptance\export-crash-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function Same($Actual, $Expected, [string]$Label) { if ($Actual -ne $Expected) { throw "$Label did not match." } }
function Canonical($Value) { return ($Value | ConvertTo-Json -Depth 12 -Compress) }
function Start-Owned([string]$Tool, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Tool)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{child=$child;output=$child.StandardOutput.ReadToEndAsync();errors=$child.StandardError.ReadToEndAsync()}
}
function Invoke-Tool([string]$Tool, [string[]]$Arguments, [switch]$ExpectFailure) {
    $run = Start-Owned $Tool $Arguments; $child = $run.child
    try {
        if (-not $child.WaitForExit(60000)) { throw 'Owned fixture command exceeded its one-minute limit.' }
        $output = $run.output.GetAwaiter().GetResult(); $errors = $run.errors.GetAwaiter().GetResult()
        if ($ExpectFailure) { if ($child.ExitCode -eq 0) { throw 'An unsafe recovery unexpectedly succeeded.' }; return ($output + $errors) }
        if ($child.ExitCode -ne 0) { throw "Fixture command failed: $errors" }
        return ($output | ConvertFrom-Json)
    } finally {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit(10000) | Out-Null }
        $child.Dispose()
    }
}
function Protected-Inventory([string]$Root) {
    $files = @('draft.json','settings.json','provider-lock.local.json','recovery-piper.json') | ForEach-Object { Join-Path $Root $_ }
    $files += @(Get-ChildItem -LiteralPath (Join-Path $Root 'jobs') -File -Recurse | Select-Object -ExpandProperty FullName)
    return @($files | Sort-Object | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($Root,$_).Replace('\','/');bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_).Hash} })
}
function Kill-AtBoundary([string]$Root, [string]$Point, [string]$Evidence) {
    $case = Split-Path -Parent $Root; $marker = Join-Path $case 'boundary.json'
    $run = Start-Owned $fixtureHost @('export-barrier',$Root,$Point); $child = $run.child
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew(); $observed = $null
        while ($watch.Elapsed.TotalSeconds -lt 60) {
            if ($child.HasExited) { throw "Owned export child exited before its checkpoint: $($run.errors.GetAwaiter().GetResult())" }
            if (Test-Path -LiteralPath $marker -PathType Leaf) {
                try { $observed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json } catch { $observed = $null }
                if ($observed) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $observed) { throw 'No flushed export checkpoint arrived within one minute.' }
        Same $observed.processId $child.Id 'Exact retained child'
        Same $observed.command 'export-barrier' 'Exact fixture operation'
        Same $observed.root $Root 'Exact isolated workspace'
        Same $observed.checkpoint $Point 'Observed publication boundary'
        Same $observed.durableRecordHash $observed.state.durableRecordHash 'Observed durable job record'
        $refused = Invoke-Tool $Executable @('backup','--root',$Root) -ExpectFailure
        if ($refused -notmatch 'already holds') { throw 'The live child did not exclude concurrent maintenance.' }
        if ($child.HasExited) { throw 'Export child was no longer live at termination.' }
        $child.Kill()
        if (-not $child.WaitForExit(10000) -or $child.ExitCode -eq 0) { throw 'Actual abnormal child termination was not established.' }
        $after = Invoke-Tool $fixtureHost @('inspect-export',$Root)
        Same $after.durableRecordHash $observed.durableRecordHash 'Durable receipt after process loss'
        Move-Item -LiteralPath $marker -Destination (Join-Path $case $Evidence)
        return [pscustomobject]@{point=$Point;processId=$observed.processId;observedLive=$true;concurrentMaintenanceRefused=$true;terminatedThroughRetainedHandle=$true;exitCode=$child.ExitCode;state=$after}
    } finally {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit(10000) | Out-Null }
        $child.Dispose()
    }
}
$scenarios = @(
    [pscustomobject]@{point='IntentSaved';mode='normal'}, [pscustomobject]@{point='StagingCreated';mode='orphan'},
    [pscustomobject]@{point='CopyStarted';mode='normal'}, [pscustomobject]@{point='CopyProgress';mode='normal'},
    [pscustomobject]@{point='CopyFlushed';mode='normal'}, [pscustomobject]@{point='CopyVerified';mode='normal'},
    [pscustomobject]@{point='BeforeRename';mode='normal'}, [pscustomobject]@{point='Renamed';mode='normal'},
    [pscustomobject]@{point='Committed';mode='normal'}, [pscustomobject]@{point='CopyProgress';mode='replace'},
    [pscustomobject]@{point='CopyProgress';mode='change'}, [pscustomobject]@{point='CopyProgress';mode='extend'}
)
if ($Smoke) { $scenarios = @($scenarios[1],$scenarios[3],$scenarios[7],$scenarios[9]) }
$results = [Collections.Generic.List[object]]::new(); $index = 0; $kills = 0
foreach ($scenario in $scenarios) {
    $case = Join-Path $fixture ('case-{0:D2}-{1}-{2}' -f $index,$scenario.point,$scenario.mode); $index++
    $root = Join-Path $case 'private'
    Invoke-Tool $fixtureHost @('seed-export',$root) | Out-Null
    $original = Invoke-Tool $fixtureHost @('inspect-export',$root)
    $protected = Canonical (Protected-Inventory $root); $finalHash = (Get-FileHash -LiteralPath $original.privateFinal).Hash
    if ((Get-Item -LiteralPath $original.privateFinal).Length -le 81920) { throw 'Real encoded audio must cross multiple copy buffers.' }
    $unrelated = Join-Path $original.destination 'unrelated.mp3'; Set-Content -LiteralPath $unrelated -Value 'Unrelated audio sentinel'
    $unrelatedHash = (Get-FileHash -LiteralPath $unrelated).Hash
    $terminated = Kill-AtBoundary $root $scenario.point 'first-boundary.json'; $kills++
    Same $terminated.state.frozenHash $original.frozenHash 'Frozen narration after actual process loss'
    if ($scenario.point -eq 'StagingCreated') { if ($terminated.state.ExportStagingIdentity) { throw 'Ownership was persisted before the observed pre-checkpoint boundary.' } }
    elseif ($scenario.point -notin @('IntentSaved','Committed')) { if (-not $terminated.state.ExportStagingIdentity) { throw 'The durable staging identity is missing.' } }
    Same ([bool]$terminated.state.ExportCommitted) ($scenario.point -eq 'Committed') 'Durable export commit flag'
    $secondary = $null
    if ($scenario.point -eq 'CopyProgress' -and $scenario.mode -eq 'normal') {
        $secondary = Kill-AtBoundary $root 'StagingResumed' 'second-boundary.json'; $kills++
        Same (Canonical $secondary.state.ExportStagingIdentity) (Canonical $terminated.state.ExportStagingIdentity) 'Stable identity through a second recovery loss'
    }
    $staging = $terminated.state.staging; $originalStaging = $null
    if ($scenario.mode -eq 'replace') {
        $originalStaging = Join-Path $case 'original-staging-retained.partial'
        Move-Item -LiteralPath $staging -Destination $originalStaging
        Copy-Item -LiteralPath $originalStaging -Destination $staging
    } elseif ($scenario.mode -eq 'change') {
        $writer = [IO.File]::Open($staging,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $writer.WriteByte(255); $writer.Flush($true) } finally { $writer.Dispose() }
    } elseif ($scenario.mode -eq 'extend') {
        Copy-Item -LiteralPath $original.privateFinal -Destination $staging -Force
        $writer = [IO.File]::Open($staging,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $writer.WriteByte(99); $writer.Flush($true) } finally { $writer.Dispose() }
    }
    if ($scenario.mode -in @('orphan','replace','change','extend')) {
        $stagingHash = (Get-FileHash -LiteralPath $staging).Hash
        $refusal = Invoke-Tool $fixtureHost @('export',$root) -ExpectFailure
        if ($refusal -notmatch 'unrecognized or changed.*staging') { throw 'Unsafe staging did not produce the expected actionable refusal.' }
        Same (Get-FileHash -LiteralPath $staging).Hash $stagingHash 'Unrecognized or altered staging preservation'
        $state = Invoke-Tool $fixtureHost @('inspect-export',$root)
        if ($state.ExportCommitted -or (Test-Path -LiteralPath (Join-Path $state.destination $state.ExportName))) { throw 'Unsafe staging became an exported MP3.' }
        if ($scenario.mode -eq 'replace') {
            $replacementKept = Join-Path $case 'unrecognized-replacement-retained.partial'
            Move-Item -LiteralPath $staging -Destination $replacementKept
            Move-Item -LiteralPath $originalStaging -Destination $staging
            $state = Invoke-Tool $fixtureHost @('export',$root)
            Same (Get-FileHash -LiteralPath $replacementKept).Hash $stagingHash 'Replacement retained after original-file recovery'
        }
    } else { $state = Invoke-Tool $fixtureHost @('export',$root) }
    if ($scenario.mode -in @('normal','replace')) {
        if (-not $state.ExportCommitted -or $state.ExportStagingIdentity -or (Test-Path -LiteralPath $staging)) { throw 'Verified export recovery did not settle.' }
        $published = Join-Path $state.destination $state.ExportName
        Same (Get-FileHash -LiteralPath $published).Hash $finalHash 'Exact completed MP3 after recovery'
        $repeated = Invoke-Tool $fixtureHost @('export',$root)
        Same $repeated.ExportName $state.ExportName 'Idempotent export name'
        Same (@(Get-ChildItem -LiteralPath $state.destination -Filter '*.mp3' -File).Count) 2 'Exactly one managed export plus the unrelated sentinel'
    }
    Same $state.frozenHash $original.frozenHash 'Immutable source, settings, timestamp, chunks and final receipt'
    Same (Canonical (Protected-Inventory $root)) $protected 'Every private source/settings/PCM/MP3 file'
    Same (Get-FileHash -LiteralPath $unrelated).Hash $unrelatedHash 'Unrelated destination bytes'
    $results.Add([pscustomobject]@{passed=$true;point=$scenario.point;mode=$scenario.mode;firstTermination=$terminated;recoveryTermination=$secondary;committed=[bool]$state.ExportCommitted;privateStatePreserved=$true;unrelatedFilePreserved=$true})
}
$report = [ordered]@{passed=$true;fixture=$fixture;cases=$results.Count;actualProcessTerminations=$kills;fixtureHost=$fixtureHost;maintenanceLeaseExecutable=$Executable;
    sourceCommit=(git -C $projectRoot rev-parse HEAD).Trim();sourceDirty=[bool](git -C $projectRoot status --porcelain);scope='Actual retained Windows child termination during real FFmpeg MP3 publication/recovery; no speech service, WPF UI, sleep/power-loss or OneDrive cloud acceptance';results=@($results);createdUtc=[datetime]::UtcNow.ToString('O')}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
[pscustomobject]@{passed=$true;fixture=$fixture;cases=$results.Count;actualProcessTerminations=$kills;sourceDirty=$report.sourceDirty} | ConvertTo-Json
