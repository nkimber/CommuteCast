param([string]$Output)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $projectRoot 'artifacts\web-release' }
$Output = [IO.Path]::GetFullPath($Output)
& dotnet publish (Join-Path $projectRoot 'src\CommuteCast.Web') -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'CommuteCast web publish failed.' }
Write-Output "Published web host: $Output"
Write-Output 'Run CommuteCast.Web.exe from this folder. It listens on http://127.0.0.1:5188 by default.'
