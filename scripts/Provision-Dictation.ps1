param([switch]$Build)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$context = (& docker context inspect --format '{{.Endpoints.docker.Host}}')
if ($LASTEXITCODE -ne 0 -or $context.Trim() -ne 'npipe:////./pipe/dockerDesktopLinuxEngine') { throw 'Use the local Docker Desktop Linux context.' }
$compose = Join-Path $projectRoot 'services\compose.yaml'
# Refuse a name collision rather than replace somebody else's container.
$existing = & docker container ls -a --filter 'name=^/commutecast-whisper$' --format '{{.ID}}'
if ($LASTEXITCODE -ne 0) { throw 'Docker is unavailable.' }
if ($existing) {
    $metadata = (& docker inspect commutecast-whisper | ConvertFrom-Json)[0]
    if ($metadata.Config.Labels.'com.commutecast.owner' -ne 'CommuteCast' -or
        $metadata.Config.Labels.'com.docker.compose.project' -ne 'commutecast' -or
        $metadata.Config.Labels.'com.docker.compose.service' -ne 'whisper') { throw 'The Whisper container name is owned by another service. No container was changed.' }
}
if ($Build) {
    & docker compose -f $compose --profile dictation build whisper
    if ($LASTEXITCODE -ne 0) { throw 'Whisper image/model build failed. Existing containers were retained.' }
}
& docker compose -f $compose --profile dictation up -d --no-deps --pull never --wait --wait-timeout 120 whisper
if ($LASTEXITCODE -ne 0) { throw 'Whisper did not become healthy. Inspect docker logs commutecast-whisper and retry.' }
$health = Invoke-RestMethod 'http://127.0.0.1:8767/health' -TimeoutSec 5
if ($health.service -ne 'commutecast-whisper' -or $health.contract -ne 1 -or $health.state -ne 'ready' -or $health.revision -ne 'd1d751a5f8271d482d14ca55d9e2deeebbae577f') { throw 'Whisper returned an incompatible health identity.' }
Write-Host 'Local English dictation is ready at 127.0.0.1:8767. Model is stored in the image; audio and transcripts are not saved by the service.'
