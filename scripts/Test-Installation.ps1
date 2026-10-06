param([string]$Executable, [switch]$KeepInstalled)
$ErrorActionPreference = 'Stop'
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
Copy-Item -LiteralPath $portable -Destination $a -Recurse
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
$sealed = Invoke-Tool -Arguments @('seal-package', '--package', $b)
$original = Fixture-State seed original
$held = [IO.FileStream]::new((Join-Path $privateRoot 'instance.lease'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $refused = Invoke-Tool -Arguments @('install', '--root', $privateRoot, '--install-root', $installRoot, '--package', $a) -ExpectedExit 1 }
finally { $held.Dispose() }
if ($refused -notmatch 'already holds') { throw 'Running workspace did not exclude installation.' }
$first = Invoke-Tool -Arguments @('install', '--root', $privateRoot, '--install-root', $installRoot, '--package', $a)
$second = Invoke-Tool -Arguments @('install', '--install-root', $installRoot, '--package', $b)
Same $second.State.Previous.PackageId $first.State.CurrentPackageId 'Previous binary identity'
$later = Fixture-State seed later
$beforePointer = (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash
$unconfirmed = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot) -ExpectedExit 1
if ($unconfirmed -notmatch 'requires.*confirm-replace') { throw 'Rollback did not require explicit replacement intent.' }
Same (Get-FileHash -LiteralPath (Join-Path $installRoot 'installation.json')).Hash $beforePointer 'Unconfirmed rollback preserves activation'
$rolledBack = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot, '--confirm-replace-local-data')
Same $rolledBack.State.CurrentPackageId $first.State.CurrentPackageId 'Rollback binary identity'
$restored = Fixture-State inspect
Same $restored.jobs.Count 1 'Pre-update queued job count'
Same $restored.jobs[0].immutablePayloadHash $original.jobs[0].immutablePayloadHash 'Frozen queued job payload'
Same $restored.jobs[0].chunks[0].hash $original.jobs[0].chunks[0].hash 'Original checked PCM bytes'
foreach ($field in @('draftHash', 'settingsHash', 'providerPinHash')) { Same $restored.$field $original.$field $field }
$undone = Invoke-Tool -Arguments @('rollback', '--install-root', $installRoot, '--confirm-replace-local-data')
Same $undone.State.CurrentPackageId $second.State.CurrentPackageId 'Undo rollback binary identity'
Same ((Fixture-State inspect).jobs.Count) 2 'Later queued jobs retained by undo snapshot'
$export = Join-Path $fixture 'separate-exports\keep-export.txt'; Set-Content -LiteralPath $export -Value 'Separate export sentinel'
$note = Join-Path $privateRoot 'keep-unrelated.txt'; Set-Content -LiteralPath $note -Value 'Unrelated local sentinel'
$model = Join-Path $privateRoot 'provisioning-models\keep.txt'; New-Item -ItemType Directory -Path (Split-Path -Parent $model) | Out-Null; Set-Content -LiteralPath $model -Value 'Separate model sentinel'
if (-not $KeepInstalled) {
    $retained = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'retain')
    if (Test-Path -LiteralPath $undone.Executable) { throw 'Tracked application binary survived uninstall.' }
    Same ((Fixture-State inspect).jobs.Count) 2 'Retain-data uninstall keeps queued jobs'
    $reinstalled = Invoke-Tool -Arguments @('install', '--install-root', $installRoot, '--package', $a)
    $removeRefused = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'remove') -ExpectedExit 1
    if ($removeRefused -notmatch 'requires.*confirm-remove') { throw 'Private data removal was not explicitly gated.' }
    $removed = Invoke-Tool -Arguments @('uninstall', '--install-root', $installRoot, '--confirm-uninstall', '--local-data', 'remove', '--confirm-remove-local-data')
    foreach ($name in @('queue.db', 'jobs', 'draft.json', 'settings.json', 'backups', 'recovery')) { if (Test-Path -LiteralPath (Join-Path $privateRoot $name)) { throw "Managed private scope $name survived requested removal." } }
    Same (Get-Content -LiteralPath $export -Raw).Trim() 'Separate export sentinel' 'Export preservation'
    Same (Get-Content -LiteralPath $note -Raw).Trim() 'Unrelated local sentinel' 'Unrelated-root preservation'
    Same (Get-Content -LiteralPath $model -Raw).Trim() 'Separate model sentinel' 'Model preservation'
}
$report = [ordered]@{ passed = $true; fixture = $fixture; privateRoot = $privateRoot; installRoot = $installRoot; executable = $undone.Executable; keptInstalledForNativeCheck = [bool]$KeepInstalled; checks = @('real-process workspace exclusion', 'versioned initial install and update', 'queued source/settings/time/receipt/artifact preservation', 'explicit compatible state-and-binary rollback', 'undo snapshot retains later jobs'); uninstallScopesExecuted = (-not $KeepInstalled); sourcePackage = $portable; createdUtc = [datetime]::UtcNow.ToString('O') }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
