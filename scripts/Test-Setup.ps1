param([string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64\app\CommuteCast.Maintenance.exe' }
$fixture = Join-Path $projectRoot ('artifacts\setup-acceptance\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$absent = Join-Path $fixture 'absent-private'
function Inspect-Setup([string]$Root) {
    $response = & $Executable check-setup --root $Root 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Setup inspection failed: $response" }
    return (($response -join "`n") | ConvertFrom-Json)
}
$emptyReport = Inspect-Setup $absent
if (Test-Path -LiteralPath $absent) { throw 'Read-only setup created the absent private folder.' }
$privateRoot = Join-Path $fixture 'private'
New-Item -ItemType Directory -Path $privateRoot | Out-Null
@{ Engine='piper'; Voice='en_US-lessac-medium'; Destination='Synthetic corporate path sentinel'; QueuePaused=$true; Ffmpeg='ffmpeg'; Ffprobe='ffprobe' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $privateRoot 'settings.json')
@('Synthetic private title','Synthetic private source sentinel') | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $privateRoot 'draft.json')
@{engine='piper';startedUtc=[datetime]::UtcNow;allowanceSeconds=120;desktopLaunches=1;ownedStarts=1} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $privateRoot 'recovery-piper.json')
function Inventory {
    return @(Get-ChildItem -LiteralPath $privateRoot -File -Recurse | ForEach-Object { [ordered]@{path=$_.FullName;hash=(Get-FileHash -LiteralPath $_.FullName).Hash;bytes=$_.Length} }) | ConvertTo-Json -Depth 4 -Compress
}
$before = Inventory
$report = Inspect-Setup $privateRoot
if ((Inventory) -ne $before) { throw 'Setup inspection changed private state.' }
$serialized = $report | ConvertTo-Json -Depth 8
foreach ($sensitive in @($privateRoot,'Synthetic corporate path sentinel','Synthetic private title','Synthetic private source sentinel')) { if ($serialized.Contains($sensitive)) { throw 'Setup report exposed private data.' } }
if (-not ($report.Checks | Where-Object {$_.Id -eq 'policy' -and $_.Status -eq 'ReviewRequired'})) {throw 'Report implied corporate approval.'}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'setup.json') -Encoding utf8
$evidence = [ordered]@{passed=$true;fixture=$fixture;absentRootPreserved=$true;privateStateUnchanged=$true;reportRedacted=$true;checks=$report.Checks | Select-Object Id,Status;createdUtc=[datetime]::UtcNow.ToString('O')}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture 'report.json') -Encoding utf8
$evidence | ConvertTo-Json -Depth 5
