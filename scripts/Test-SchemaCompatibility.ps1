param([Parameter(Mandatory)][string]$OlderMaintenance, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$OlderMaintenance = [IO.Path]::GetFullPath($OlderMaintenance)
if (-not (Test-Path -LiteralPath $OlderMaintenance -PathType Leaf)) { throw 'Choose a retained older packaged maintenance executable.' }
if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Schema fixture build failed.' }
}
$fixtureHost = Join-Path $projectRoot 'tools/CommuteCast.AcceptanceFixtures/bin/Release/net10.0/CommuteCast.AcceptanceFixtures.exe'
$evidence = Join-Path $projectRoot ('artifacts/installation-acceptance/schema-compatibility-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $evidence 'private'; New-Item -ItemType Directory -Path $evidence | Out-Null
$seedOutput = & $fixtureHost seed $root
if ($LASTEXITCODE -ne 0) { throw 'New schema fixture seed failed.' }
function Protected-Inventory {
    $files = @('queue.db','draft.json','settings.json','provider-lock.local.json','recovery-piper.json') | ForEach-Object { Join-Path $root $_ }
    $files += @(Get-ChildItem -LiteralPath (Join-Path $root 'jobs') -File -Recurse | Select-Object -ExpandProperty FullName)
    return @($files | Sort-Object | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($root,$_);bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_).Hash} })
}
$before = Protected-Inventory
$start = [Diagnostics.ProcessStartInfo]::new($OlderMaintenance)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in @('backup','--root',$root)) { $start.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($start); $outputTask = $child.StandardOutput.ReadToEndAsync(); $errorTask = $child.StandardError.ReadToEndAsync()
try {
    if (-not $child.WaitForExit(30000)) { throw 'Older maintenance refusal exceeded 30 seconds.' }
    $output = $outputTask.GetAwaiter().GetResult(); $errors = $errorTask.GetAwaiter().GetResult()
    if ($child.ExitCode -eq 0 -or ($output + $errors) -notmatch 'Queue schema 2 needs a newer CommuteCast version') { throw "Older maintenance did not refuse the new schema: $output $errors" }
    $exitCode = $child.ExitCode
} finally {
    if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit(10000) | Out-Null }
    $child.Dispose()
}
$after = Protected-Inventory
if (($before | ConvertTo-Json -Depth 5 -Compress) -ne ($after | ConvertTo-Json -Depth 5 -Compress)) { throw 'Older maintenance changed protected local state.' }
$inspectionOutput = & $fixtureHost inspect $root
if ($LASTEXITCODE -ne 0) { throw 'The current runtime could not reopen its preserved fixture.' }
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim(); $dirty = [bool](& git -C $projectRoot status --porcelain)
$newBuild = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixtureHost).ProductVersion
if (-not $dirty -and $newBuild -ne ('0.1.0+' + $sourceCommit)) { throw 'Rebuild the committed fixture before recording clean-source evidence.' }
$report = [ordered]@{sourceCommit=$sourceCommit;newBuild=$newBuild;workingTreeDirty=$dirty;olderExecutable=$OlderMaintenance;olderBuild=[Diagnostics.FileVersionInfo]::GetVersionInfo($OlderMaintenance).ProductVersion;olderExecutableSha256=(Get-FileHash -LiteralPath $OlderMaintenance).Hash;newSchema=2;olderRefused=$true;exitCode=$exitCode;protectedStateUnchanged=$true;newRuntimeReopened=$true;protectedFiles=$before;scope='Actual retained schema-one maintenance refusal of an isolated schema-two queue. Native older-desktop launch and corporate downgrade deployment remain separate.'}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
Write-Output (Join-Path $evidence 'report.json')
