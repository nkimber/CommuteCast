param([string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64\app\CommuteCast.Maintenance.exe' }
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Publish the portable application first, or supply -Executable.' }
$fixture = Join-Path $projectRoot ('artifacts\maintenance-acceptance\' + [guid]::NewGuid().ToString('N'))
$privateRoot = Join-Path $fixture 'private'
$outputRoot = Join-Path $fixture 'separate-exports'
New-Item -ItemType Directory -Path $privateRoot, $outputRoot | Out-Null
function Invoke-Maintenance([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $response = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne $ExpectedExit) { throw "Maintenance exit $LASTEXITCODE, expected $ExpectedExit. $response" }
    if ($ExpectedExit -eq 0) { return (($response -join "`n") | ConvertFrom-Json) }
    return ($response -join "`n")
}
function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) { throw "$Label did not match." }
}
$draft = Join-Path $privateRoot 'draft.json'
$settings = Join-Path $privateRoot 'settings.json'
Set-Content -LiteralPath $draft -Value '["Synthetic fixture","Saved source for maintenance acceptance"]' -Encoding utf8
Set-Content -LiteralPath $settings -Value '{"Engine":"piper","Voice":"en_US-lessac-medium","QueuePaused":true}' -Encoding utf8
$jobId = [guid]::NewGuid().ToString('N')
$jobFolder = Join-Path $privateRoot ('jobs\' + $jobId)
New-Item -ItemType Directory -Path $jobFolder | Out-Null
$sourceRecord = Join-Path $jobFolder 'source.txt'
Set-Content -LiteralPath $sourceRecord -Value 'Synthetic private job artifact' -Encoding utf8
$unrelated = Join-Path $privateRoot 'unrelated.txt'
$export = Join-Path $outputRoot 'export-sentinel.txt'
Set-Content -LiteralPath $unrelated -Value 'Unrelated state remains'
Set-Content -LiteralPath $export -Value 'Separate output remains'
$protectedBefore = @{}; foreach ($path in @($draft, $settings, $sourceRecord, $unrelated, $export)) { $protectedBefore[$path] = (Get-FileHash -LiteralPath $path).Hash }

# A real second process must refuse a held lease before changing managed state.
$held = [IO.FileStream]::new((Join-Path $privateRoot 'instance.lease'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $refusal = Invoke-Maintenance -Arguments @('backup', '--root', $privateRoot) -ExpectedExit 1 }
finally { $held.Dispose() }
if ($refusal -notmatch 'already holds') { throw 'Held workspace was not refused.' }
if (Test-Path -LiteralPath (Join-Path $privateRoot 'queue.db')) { throw 'Held workspace was modified.' }

$created = Invoke-Maintenance -Arguments @('backup', '--root', $privateRoot)
$validated = Invoke-Maintenance -Arguments @('validate-backup', '--backup', $created.backup)
Assert-Equal $validated.valid $true 'Completed backup validation'
Assert-Equal $validated.files 4 'Current-state backup scope'
$queue = Join-Path $privateRoot 'queue.db'; $queueHash = (Get-FileHash -LiteralPath $queue).Hash
$snapshotHash = (Get-FileHash -LiteralPath (Join-Path $created.backup 'queue.db')).Hash
Set-Content -LiteralPath $draft -Value '["Later draft","Later synthetic source"]' -Encoding utf8
Set-Content -LiteralPath $sourceRecord -Value 'Later private artifact' -Encoding utf8
$laterHash = (Get-FileHash -LiteralPath $draft).Hash
$missingConfirmation = Invoke-Maintenance -Arguments @('restore', '--root', $privateRoot, '--backup', $created.backup) -ExpectedExit 1
if ($missingConfirmation -notmatch 'Explicit.*required') { throw 'Restore did not require its explicit replacement option.' }
Assert-Equal (Get-FileHash -LiteralPath $draft).Hash $laterHash 'Unconfirmed restore preserves state'
$restored = Invoke-Maintenance -Arguments @('restore', '--root', $privateRoot, '--backup', $created.backup, '--confirm-replace-local-data')
Assert-Equal $restored.restored $true 'Confirmed restore'
Assert-Equal (Get-FileHash -LiteralPath $queue).Hash $snapshotHash 'Consistent database snapshot restore'
Assert-Equal (Get-FileHash -LiteralPath (Join-Path $restored.PreviousState 'queue.db')).Hash $queueHash 'Retained previous database'
foreach ($path in $protectedBefore.Keys) { Assert-Equal (Get-FileHash -LiteralPath $path).Hash $protectedBefore[$path] 'Original artifact preservation' }
Assert-Equal (Get-FileHash -LiteralPath (Join-Path $restored.PreviousState 'draft.json')).Hash $laterHash 'Retained previous draft'
$recovered = Invoke-Maintenance -Arguments @('recover', '--root', $privateRoot)
Assert-Equal $recovered.recoveredInterruptedRestore $false 'No unfinished restore'
$report = [ordered]@{ passed = $true; executable = (Resolve-Path -LiteralPath $Executable).Path; fixture = $fixture; backup = $created.backup; previousState = $restored.PreviousState; checks = @('cross-process lease exclusion', 'backup validation and scope', 'explicit replacement required', 'database and private-file checksum restoration', 'previous-state retention', 'unrelated and separate-output preservation', 'idempotent recovery'); audioValidation = 'Covered separately by WorkspaceBackupTests with real FFmpeg audio'; createdUtc = [datetime]::UtcNow.ToString('O') }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
