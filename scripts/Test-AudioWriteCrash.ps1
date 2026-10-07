param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Audio fixture build failed.' }
}
$fixtureHost = Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures/bin/Release/net10.0/CommuteCast.AcceptanceFixtures.exe'
$evidence = Join-Path $projectRoot ('artifacts/installation-acceptance/audio-write-' + [guid]::NewGuid().ToString('N'))
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
foreach ($format in @('wav','mp3')) {
    $case = Join-Path $evidence $format; $root = Join-Path $case 'private'; $marker = Join-Path $case 'boundary.json'
    New-Item -ItemType Directory -Path $case | Out-Null
    $run = Start-Owned @('audio-write-barrier',$root,$format); $encoder = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            if ($run.child.HasExited) { throw ('Audio host exited early: ' + $run.errors.GetAwaiter().GetResult()) }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Audio boundary was not observed within 30 seconds.' }
            Start-Sleep -Milliseconds 50
        }
        $boundary = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($boundary.processId -ne $run.child.Id -or $boundary.format -ne $format -or $run.child.HasExited -or $boundary.observedBytes -lt 8192) { throw 'Boundary does not identify active owned audio bytes.' }
        $encoder = [Diagnostics.Process]::GetProcessById($boundary.childId)
        if ($encoder.HasExited -or $encoder.ProcessName -ne 'ffmpeg' -or $encoder.StartTime.ToUniversalTime().Ticks -ne ([DateTime]$boundary.childStartUtc).ToUniversalTime().Ticks) { throw 'Recorded encoder identity did not match the live child.' }
        # Kill only the owned fixture host. Its Windows job must terminate FFmpeg.
        $run.child.Kill($false)
        if (-not $run.child.WaitForExit(10000) -or -not $encoder.WaitForExit(10000)) { throw 'Parent-only loss did not terminate the owned encoder.' }
        $null = $run.output.GetAwaiter().GetResult(); $null = $run.errors.GetAwaiter().GetResult()
    } finally {
        if (-not $run.child.HasExited) { $run.child.Kill($false); $run.child.WaitForExit(10000) | Out-Null }
        $run.child.Dispose()
        if ($null -ne $encoder) { $encoder.Dispose() }
    }
    $recovery = Start-Owned @('audio-write-recover',$root,$format)
    try {
        if (-not $recovery.child.WaitForExit(30000)) { throw 'Audio write recovery exceeded 30 seconds.' }
        $output = $recovery.output.GetAwaiter().GetResult(); $errors = $recovery.errors.GetAwaiter().GetResult()
        if ($recovery.child.ExitCode -ne 0) { throw "Audio recovery failed: $errors" }
        $result = $output | ConvertFrom-Json
        if ($result.Id -ne $boundary.Id -or $result.applicationBuild -ne $boundary.applicationBuild -or -not $result.exactRecovery -or -not $result.durableRetry -or -not $result.unrelatedPreserved) { throw 'Audio recovery did not match the observed boundary.' }
        $results += [ordered]@{format=$format;parentOnlyLoss=$true;ownedEncoderExited=$true;boundary=$boundary;recovery=$result}
    } finally {
        if (-not $recovery.child.HasExited) { $recovery.child.Kill($false); $recovery.child.WaitForExit(10000) | Out-Null }
        $recovery.child.Dispose()
    }
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim(); $dirty = [bool](& git -C $projectRoot status --porcelain)
if (-not $dirty -and $result.applicationBuild -ne ('0.1.0+' + $sourceCommit)) { throw 'Rebuild the committed fixture before recording clean-source evidence.' }
$report = [ordered]@{sourceCommit=$sourceCommit;applicationBuild=$result.applicationBuild;workingTreeDirty=$dirty;cases=$results.Count;passed=$true;results=$results;scope='Parent-only host loss during actual synthetic FFmpeg WAV and MP3 held-file writes, job-contained child exit, exact incomplete receipt reconciliation and durable retry. Production queue/unit integration is checked separately; real engines, auditions and pre-identity checkpoint loss remain separate.'}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
Write-Output (Join-Path $evidence 'report.json')
