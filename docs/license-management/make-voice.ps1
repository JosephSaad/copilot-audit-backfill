<#
    Speaks each scene's narration (scenes.json) into audio\NN.wav with a built-in Windows voice (16 kHz mono),
    and writes each clip's length back into scenes.json (durationMs), which the player uses to time subtitles.
#>
param([string] $Voice = 'Microsoft Zira Desktop', [int] $Rate = 0)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$dir = $PSScriptRoot
$audio = Join-Path $dir 'audio'
New-Item -ItemType Directory -Force $audio | Out-Null
$scenes = Get-Content (Join-Path $dir 'scenes.json') -Raw | ConvertFrom-Json
$fmt = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
foreach ($s in $scenes) {
    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    $synth.SelectVoice($Voice)
    $synth.Rate = $Rate
    $file = Join-Path $audio ('{0:D2}.wav' -f $s.n)
    $synth.SetOutputToWaveFile($file, $fmt)
    $synth.Speak($s.narration)
    $synth.Dispose()
    $bytes = (Get-Item $file).Length - 44
    $ms = [int]($bytes / 32000 * 1000)
    $s | Add-Member -NotePropertyName audio -NotePropertyValue ('audio/{0:D2}.wav' -f $s.n) -Force
    $s | Add-Member -NotePropertyName durationMs -NotePropertyValue $ms -Force
    '{0:D2} {1,6} ms  {2}' -f $s.n, $ms, $s.title
}
$scenes | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dir 'scenes.json') -Encoding utf8NoBOM
