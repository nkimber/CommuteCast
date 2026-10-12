param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\dictation-acceptance' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$health = Invoke-RestMethod 'http://127.0.0.1:8767/health' -TimeoutSec 5
if ($health.service -ne 'commutecast-whisper' -or $health.state -ne 'ready' -or $health.model -ne 'small.en') { throw 'Local Whisper is not ready.' }
Add-Type -AssemblyName System.Speech
$sample = Join-Path $OutputDirectory 'known-goal.wav'
$speaker = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $speaker.SetOutputToWaveFile($sample, $format)
    $speaker.Speak('The listener should understand how railways changed cities and learn why public transport matters.')
} finally { $speaker.Dispose() }
$timer = [Diagnostics.Stopwatch]::StartNew()
$transcript = Invoke-RestMethod 'http://127.0.0.1:8767/transcribe' -Method Post -InFile $sample -ContentType 'audio/wav' -TimeoutSec 180
$timer.Stop()
if ($transcript.text -notmatch 'railways' -or $transcript.text -notmatch 'cities' -or $transcript.text -notmatch 'public transport') { throw 'Known speech was not transcribed as expected.' }
$report = [ordered]@{ Health = $health; Audio = $sample; Transcript = $transcript.text; TranscriptionSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2) }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json')
$report | ConvertTo-Json -Depth 5
