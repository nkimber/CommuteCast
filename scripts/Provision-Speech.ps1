param([switch]$Build)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$context = (& docker context inspect --format '{{.Endpoints.docker.Host}}')
if ($LASTEXITCODE -ne 0 -or $context -notlike 'npipe://*') { throw 'Use the local Docker Desktop context.' }
if ($Build) {
    $modelCache = Join-Path $env:LOCALAPPDATA 'CommuteCast\provisioning-models'
    $modelSources = @{
        'kokoro/kokoro-v1.0.onnx' = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx'
        'kokoro/voices-v1.0.bin' = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin'
        'piper/en_US-lessac-medium.onnx' = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx'
        'piper/en_US-lessac-medium.onnx.json' = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx.json'
    }
    foreach ($line in Get-Content -LiteralPath (Join-Path $projectRoot 'services\speech\model-checksums.txt')) {
        $parts = $line -split '\s+', 2
        $relative = $parts[1].Trim()
        if (-not $modelSources.ContainsKey($relative)) { throw 'Unrecognized model artifact.' }
        $target = Join-Path $modelCache $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $parts[0]) { continue }
        Write-Host "Acquiring pinned model: $relative"
        $partial = $target + '.partial'
        Invoke-WebRequest -Uri $modelSources[$relative] -OutFile $partial -TimeoutSec 900
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $parts[0]) { throw "Model checksum mismatch: $relative. The original file was preserved." }
        Move-Item -LiteralPath $partial -Destination $target -Force
    }
    & docker build --build-context "commutecast_models=$modelCache" -t commutecast-speech:1 (Join-Path $projectRoot 'services\speech')
    if ($LASTEXITCODE -ne 0) { throw 'Speech image build failed.' }
}
& docker compose -f (Join-Path $projectRoot 'services\compose.yaml') up -d --pull never
if ($LASTEXITCODE -ne 0) { throw 'Service provisioning failed. Existing services were not removed.' }
$imageId = (& docker image inspect commutecast-speech:1 --format '{{.Id}}')
if ($LASTEXITCODE -ne 0) { throw 'Image identity could not be read.' }
$dataDirectory = Join-Path $env:LOCALAPPDATA 'CommuteCast'
New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
@{ ImageId = $imageId.Trim(); Contract = 1 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataDirectory 'provider-lock.local.json')
Write-Host 'CommuteCast services provisioned. Image identity pinned locally. The app will only start these verified containers.'
