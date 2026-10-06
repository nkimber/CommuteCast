$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64'
$application = Join-Path $release 'app'
& dotnet publish (Join-Path $projectRoot 'src\CommuteCast.Desktop') -c Release -r win-x64 --self-contained true -o $application
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
& dotnet publish (Join-Path $projectRoot 'tools\CommuteCast.Maintenance') -c Release -r win-x64 --self-contained true -o $application
if ($LASTEXITCODE -ne 0) { throw 'Maintenance publish failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $release 'scripts'), (Join-Path $release 'services'), (Join-Path $release 'documents') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $release
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\Start-CommuteCast.ps1'), (Join-Path $projectRoot 'scripts\Provision-Speech.ps1') -Destination (Join-Path $release 'scripts')
Copy-Item -LiteralPath (Join-Path $projectRoot 'services\compose.yaml') -Destination (Join-Path $release 'services')
Copy-Item -LiteralPath (Join-Path $projectRoot 'services\speech') -Destination (Join-Path $release 'services') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'documents\Acceptance.md') -Destination (Join-Path $release 'documents')
Copy-Item -LiteralPath (Join-Path $projectRoot 'documents\Implementation-Decisions.md'), (Join-Path $projectRoot 'documents\Verification-Matrix.md') -Destination (Join-Path $release 'documents')
& (Join-Path $application 'CommuteCast.Maintenance.exe') seal-package --package $release
if ($LASTEXITCODE -ne 0) { throw 'Release inventory or integrity verification failed.' }
Compress-Archive -Path (Join-Path $release '*') -DestinationPath (Join-Path $projectRoot 'artifacts\release\CommuteCast-win-x64.zip') -Force
Write-Host "Portable application: $application"
