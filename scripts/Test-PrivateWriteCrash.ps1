param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Private write fixture build failed.' }
}
$fixtureHost = Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures/bin/Release/net10.0/CommuteCast.AcceptanceFixtures.exe'
$evidence = Join-Path $projectRoot ('artifacts/installation-acceptance/private-write-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
function Start-Owned([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($fixtureHost)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{child=$child;output=$child.StandardOutput.ReadToEndAsync();errors=$child.StandardError.ReadToEndAsync()}
}
$results = @()
foreach ($point in @('before-write','during-write','before-complete-save','after-complete-save')) {
    $case = Join-Path $evidence $point; $root = Join-Path $case 'private'; $marker = Join-Path $case 'boundary.json'
    New-Item -ItemType Directory -Path $case | Out-Null
    $run = Start-Owned @('write-barrier',$root,$point)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            if ($run.child.HasExited) { throw ('Owned write barrier exited early: ' + $run.errors.GetAwaiter().GetResult()) }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Write boundary was not observed within 30 seconds.' }
            Start-Sleep -Milliseconds 50
        }
        $boundary = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($boundary.processId -ne $run.child.Id -or $boundary.point -ne $point -or $run.child.HasExited) { throw 'Boundary does not identify the live owned child.' }
        $run.child.Kill($true)
        if (-not $run.child.WaitForExit(10000)) { throw 'Owned child did not terminate.' }
        $null = $run.output.GetAwaiter().GetResult(); $null = $run.errors.GetAwaiter().GetResult()
    } finally {
        if (-not $run.child.HasExited) { $run.child.Kill($true); $run.child.WaitForExit(10000) | Out-Null }
        $run.child.Dispose()
    }
    $recovery = Start-Owned @('write-recover',$root)
    try {
        if (-not $recovery.child.WaitForExit(30000)) { throw 'Private write recovery exceeded 30 seconds.' }
        $output = $recovery.output.GetAwaiter().GetResult(); $errors = $recovery.errors.GetAwaiter().GetResult()
        if ($recovery.child.ExitCode -ne 0) { throw "Private write recovery failed: $errors" }
        $result = $output | ConvertFrom-Json
        if ($result.Id -ne $boundary.Id -or $result.applicationBuild -ne $boundary.applicationBuild -or $result.incomplete -ne ($point -ne 'after-complete-save') -or
            -not $result.exactRecovery -or -not $result.durableRetry -or -not $result.unrelatedPreserved) { throw 'Write recovery did not match the observed boundary.' }
        $results += [ordered]@{point=$point;boundary=$boundary;recovery=$result}
    } finally {
        if (-not $recovery.child.HasExited) { $recovery.child.Kill($true); $recovery.child.WaitForExit(10000) | Out-Null }
        $recovery.child.Dispose()
    }
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim(); $dirty = [bool](& git -C $projectRoot status --porcelain)
if (-not $dirty -and $result.applicationBuild -ne ('0.1.0+' + $sourceCommit)) { throw 'Rebuild the committed fixture before recording clean-source evidence.' }
$report = [ordered]@{sourceCommit=$sourceCommit;applicationBuild=$result.applicationBuild;workingTreeDirty=$dirty;cases=$results.Count;passed=$true;results=$results;scope='Four actual owned Windows child terminations before/during writing and before/after completed receipt save. Synthetic private writer bytes; provider/normalizer/encoder/audition and pre-identity-checkpoint loss remain separate.'}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
Write-Output (Join-Path $evidence 'report.json')
