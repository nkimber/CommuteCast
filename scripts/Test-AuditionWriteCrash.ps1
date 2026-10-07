param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Preview-write fixture build failed.' }
}
$fixtureHost = Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures/bin/Release/net10.0/CommuteCast.AcceptanceFixtures.exe'
$evidence = Join-Path $projectRoot ('artifacts/installation-acceptance/audition-write-' + [guid]::NewGuid().ToString('N'))
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
foreach ($engine in @('kokoro','piper')) {
    foreach ($point in @('before-copy','during-copy','before-rename','after-rename','after-complete-save')) {
        $case = Join-Path $evidence ($engine + '-' + $point); $root = Join-Path $case 'private'; $marker = Join-Path $case 'boundary.json'
        New-Item -ItemType Directory -Path $case | Out-Null
        $run = Start-Owned @('audition-write-barrier',$root,$engine,$point)
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                if ($run.child.HasExited) { throw ('Preview boundary host exited early: ' + $run.errors.GetAwaiter().GetResult()) }
                if ([DateTime]::UtcNow -gt $deadline) { throw 'Preview boundary was not observed within 30 seconds.' }
                Start-Sleep -Milliseconds 50
            }
            $boundary = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ($boundary.processId -ne $run.child.Id -or $boundary.point -ne $point -or $boundary.engine -ne $engine -or $run.child.HasExited -or
                $boundary.reservationPresent -ne ($point -in @('before-copy','during-copy')) -or
                ($point -eq 'before-copy' -and $boundary.observedBytes -ne 0) -or ($point -eq 'during-copy' -and $boundary.observedBytes -le 0) -or
                ($point -notin @('before-copy','during-copy') -and $boundary.observedBytes -ne 96044)) { throw 'Boundary does not identify the live owned host and expected write/reservation state.' }
            $run.child.Kill($false)
            if (-not $run.child.WaitForExit(10000)) { throw 'Owned preview host did not terminate.' }
            $null = $run.output.GetAwaiter().GetResult(); $null = $run.errors.GetAwaiter().GetResult()
        } finally {
            if (-not $run.child.HasExited) { $run.child.Kill($false); $run.child.WaitForExit(10000) | Out-Null }
            $run.child.Dispose()
        }
        $recovery = Start-Owned @('audition-write-recover',$root,$engine)
        try {
            if (-not $recovery.child.WaitForExit(30000)) { throw 'Preview-write recovery exceeded 30 seconds.' }
            $output = $recovery.output.GetAwaiter().GetResult(); $errors = $recovery.errors.GetAwaiter().GetResult()
            if ($recovery.child.ExitCode -ne 0) { throw "Preview-write recovery failed: $errors" }
            $result = $output | ConvertFrom-Json
            if ($result.Id -ne $boundary.Id -or $result.applicationBuild -ne $boundary.applicationBuild -or $result.point -ne $point -or $result.engine -ne $engine -or
                $result.expectedHash -ne $boundary.expectedHash -or -not $result.exactRecovery -or -not $result.durableRetry -or -not $result.unrelatedPreserved -or
                -not $result.sourceFreeHistory -or -not $result.reservationSettled) { throw 'Preview recovery did not match the observed boundary.' }
            $results += [ordered]@{engine=$engine;point=$point;parentOnlyLoss=$true;boundary=$boundary;recovery=$result}
        } finally {
            if (-not $recovery.child.HasExited) { $recovery.child.Kill($false); $recovery.child.WaitForExit(10000) | Out-Null }
            $recovery.child.Dispose()
        }
    }
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim(); $dirty = [bool](& git -C $projectRoot status --porcelain)
if (-not $dirty -and $result.applicationBuild -ne ('0.1.0+' + $sourceCommit)) { throw 'Rebuild the committed fixture before recording clean-source evidence.' }
$report = [ordered]@{sourceCommit=$sourceCommit;applicationBuild=$result.applicationBuild;workingTreeDirty=$dirty;cases=$results.Count;passed=$true;results=$results;scope='Ten actual owned preview-host terminations through production AuditionGenerator and LocalSpeechProvider. Original-file recovery, source-free history, reservation retirement and durable retry across synthetic contracts; no real Docker/HTTP/model, native playback or pre-identity-checkpoint acceptance.'}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
Write-Output (Join-Path $evidence 'report.json')
