param(
    [string]$Workspace = (Join-Path $env:LOCALAPPDATA 'CommuteCast-Web'),
    [ValidateRange(1024, 65535)][int]$Port = 5188,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\CommuteCast.Web'
if (-not $NoBuild) {
    & dotnet build $project -c Release
    if ($LASTEXITCODE -ne 0) { throw 'CommuteCast web build failed.' }
}
# The listener is private to this computer. Cloudflare Tunnel connects to this port.
& dotnet run --project $project -c Release --no-build --no-launch-profile --urls "http://127.0.0.1:$Port" --CommuteCast:Workspace ([IO.Path]::GetFullPath($Workspace))
if ($LASTEXITCODE -ne 0) { throw "CommuteCast web exited with code $LASTEXITCODE." }
