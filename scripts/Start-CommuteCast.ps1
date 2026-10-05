$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$portable = Join-Path $projectRoot 'app\CommuteCast.Desktop.exe'
if (Test-Path -LiteralPath $portable) {
    Start-Process -FilePath $portable -WorkingDirectory $projectRoot -WindowStyle Hidden
    return
}
& dotnet build (Join-Path $projectRoot 'src\CommuteCast.Desktop') -c Release
if ($LASTEXITCODE -ne 0) { throw 'CommuteCast build failed.' }
$executable = Join-Path $projectRoot 'src\CommuteCast.Desktop\bin\Release\net10.0-windows\CommuteCast.Desktop.exe'
Start-Process -FilePath $executable -WorkingDirectory $projectRoot -WindowStyle Hidden
