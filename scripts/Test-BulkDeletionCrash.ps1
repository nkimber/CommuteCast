$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Bulk fixture build failed.' }
$hostPath = Join-Path $projectRoot 'tools\CommuteCast.AcceptanceFixtures\bin\Release\net10.0\CommuteCast.AcceptanceFixtures.exe'
$reportRoot = Join-Path $projectRoot ('artifacts\installation-acceptance\bulk-delete-' + [guid]::NewGuid().ToString('N'))
$cases = @()
foreach ($scope in @('private','keep-exports','remove-exports')) {
foreach ($point in @('intent-committed','first-record-removed')) {
    $caseRoot = Join-Path $reportRoot ($scope + '-' + $point); $privateRoot = Join-Path $caseRoot 'private'
    New-Item -ItemType Directory -Path $privateRoot | Out-Null
    $marker = Join-Path $caseRoot 'boundary.json'
    $start = [Diagnostics.ProcessStartInfo]::new($hostPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('bulk-delete-barrier',$privateRoot,$point)) { $start.ArgumentList.Add($argument) }
    if ($scope -ne 'private') { $start.ArgumentList.Add($scope) }
    $child = [Diagnostics.Process]::Start($start)
    $output = $child.StandardOutput.ReadToEndAsync(); $errors = $child.StandardError.ReadToEndAsync()
    try {
        $deadline = [datetime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            if ($child.HasExited) { throw ('Bulk host exited early: ' + $errors.GetAwaiter().GetResult()) }
            if ([datetime]::UtcNow -gt $deadline) { throw 'Bulk deletion checkpoint was not reached.' }
            Start-Sleep -Milliseconds 50
        }
        $observed = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        $expected = if ($point -eq 'intent-committed') { 3 } else { 2 }
        if ($child.HasExited -or $observed.processId -ne $child.Id -or $observed.point -ne $point -or $observed.remainingSelected -ne $expected) { throw 'Bulk marker does not identify the live owned host and durable selected intents.' }
        if ($observed.withExports -ne ($scope -ne 'private') -or $observed.deleteExports -ne ($scope -eq 'remove-exports')) { throw 'Bulk export consent marker differs from selected scope.' }
        $child.Kill($false)
        if (-not $child.WaitForExit(10000)) { throw 'Owned bulk host did not terminate.' }
        $null = $output.GetAwaiter().GetResult(); $null = $errors.GetAwaiter().GetResult()
    } finally {
        if (-not $child.HasExited) { $child.Kill($false); $child.WaitForExit(10000) | Out-Null }
        $child.Dispose()
    }
    $recovery = & $hostPath bulk-delete-recover $privateRoot
    if ($LASTEXITCODE -ne 0) { throw 'Actual bulk deletion recovery failed.' }
    $first = ($recovery -join "`n") | ConvertFrom-Json
    $repeated = & $hostPath bulk-delete-recover $privateRoot
    if ($LASTEXITCODE -ne 0) { throw 'Repeated bulk recovery failed.' }
    $second = ($repeated -join "`n") | ConvertFrom-Json
    if (-not $first.passed -or -not $second.passed) { throw 'Bulk recovery assertions failed.' }
    $cases += [ordered]@{passed=$true;scope=$scope;point=$point;parentOnlyLoss=$true;boundary=$observed;recovery=$first;idempotent=$true}
}
}
$report = [ordered]@{passed=$true;fixture=$reportRoot;cases=$cases;createdUtc=[datetime]::UtcNow.ToString('O')}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportRoot 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
