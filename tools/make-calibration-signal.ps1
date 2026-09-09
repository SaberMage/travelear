# Builds tools/calibration-signal.wav: the test signal the SPEAKER machine plays into the game's mic
# during a T4a reference capture (M3-PLAN). 48 kHz mono 16-bit. Play it on loop through VoiceMeeter
# (or any virtual cable) into the game's microphone device.
#
# Layout of one cycle (~14 s):
#   1. click     one-sample impulse (alignment marker; also survives Opus as a sharp onset)
#   2. speech    a spoken sentence from Windows TTS (keeps the game's voice activation open,
#                and is what the compressor / makeup gain / EQ are tuned for)
#   3. silence   2.5 s (the reverb tail with nothing under it)
#   4. click     a second impulse inside the VAD hold window (a tail with no speech masking)
#   5. sweep     2 s log sweep 120 Hz - 8 kHz at -12 dBFS (spectral shape; may or may not pass the
#                game's VAD, which is itself a finding)
#   6. silence   3 s
# Usage: pwsh tools/make-calibration-signal.ps1 [-Out tools/calibration-signal.wav] [-Cycles 4]
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot 'calibration-signal.wav'),
    [int]$Cycles = 4,
    [string]$Sentence = 'TravelEar calibration, sentence one. The quick brown fox jumps over the lazy dog, and the hallway answers back.'
)

$ErrorActionPreference = 'Stop'
$rate = 48000

# 1. Speech via Windows TTS (System.Speech is Windows-only; pwsh 7 on Windows loads it fine).
Add-Type -AssemblyName System.Speech
$tts = New-Object System.Speech.Synthesis.SpeechSynthesizer
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) 'travelear-tts.wav'
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo($rate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$tts.SetOutputToWaveFile($tmp, $fmt)
$tts.Rate = -1
$tts.Speak($Sentence)
$tts.SetOutputToNull()
$tts.Dispose()

$bytes = [System.IO.File]::ReadAllBytes($tmp)
# Minimal RIFF parse: find the "data" chunk.
$pos = 12
$speech = $null
while ($pos -lt $bytes.Length - 8) {
    $id = [System.Text.Encoding]::ASCII.GetString($bytes, $pos, 4)
    $size = [System.BitConverter]::ToInt32($bytes, $pos + 4)
    if ($id -eq 'data') {
        $n = [int]($size / 2)
        $speech = New-Object float[] $n
        for ($i = 0; $i -lt $n; $i++) { $speech[$i] = [System.BitConverter]::ToInt16($bytes, $pos + 8 + 2 * $i) / 32768.0 }
        break
    }
    $pos += 8 + $size + ($size % 2)
}
if ($null -eq $speech) { throw 'TTS output has no data chunk.' }
# Normalise speech to -6 dBFS peak.
$peak = 0.0; foreach ($v in $speech) { $a = [Math]::Abs($v); if ($a -gt $peak) { $peak = $a } }
if ($peak -gt 0) { $g = 0.5 / $peak; for ($i = 0; $i -lt $speech.Length; $i++) { $speech[$i] *= $g } }

# 2. Sweep.
$sweepLen = 2 * $rate
$sweep = New-Object float[] $sweepLen
$f0 = 120.0; $f1 = 8000.0; $T = 2.0
$k = [Math]::Log($f1 / $f0)
for ($i = 0; $i -lt $sweepLen; $i++) {
    $t = $i / $rate
    $phase = 2 * [Math]::PI * $f0 * $T / $k * ([Math]::Exp($t * $k / $T) - 1)
    $fade = 1.0
    if ($i -lt 480) { $fade = $i / 480.0 } elseif ($i -gt $sweepLen - 480) { $fade = ($sweepLen - $i) / 480.0 }
    $sweep[$i] = 0.25 * $fade * [Math]::Sin($phase)
}

# 3. Assemble cycles.
$silence25 = New-Object float[] ([int](2.5 * $rate))
$silence3 = New-Object float[] (3 * $rate)
$silence02 = New-Object float[] ([int](0.2 * $rate))
$click = New-Object float[] ([int](0.05 * $rate)); $click[0] = 0.9; $click[1] = -0.6
$signal = New-Object System.Collections.Generic.List[float]
for ($c = 0; $c -lt $Cycles; $c++) {
    $signal.AddRange($click); $signal.AddRange($silence02)
    $signal.AddRange($speech)
    $signal.AddRange($silence25)
    $signal.AddRange($click); $signal.AddRange($silence02)
    $signal.AddRange($sweep)
    $signal.AddRange($silence3)
}

# 4. Write 16-bit PCM WAV.
$n = $signal.Count
$stream = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($stream)
$w.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + 2 * $n)); $w.Write([System.Text.Encoding]::ASCII.GetBytes('WAVE'))
$w.Write([System.Text.Encoding]::ASCII.GetBytes('fmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int](2 * $rate)); $w.Write([int16]2); $w.Write([int16]16)
$w.Write([System.Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int](2 * $n))
$nan = 0
foreach ($v in $signal) { if ([double]::IsNaN($v)) { $nan++; $v = 0.0 }; $s = [Math]::Max(-1.0, [Math]::Min(1.0, [double]$v)); $w.Write([int16][Math]::Round($s * 32767)) }
$w.Dispose(); $stream.Dispose()
Remove-Item $tmp -ErrorAction SilentlyContinue
Write-Host ("Wrote {0}: {1:F1} s, {2} cycles, speech {3:F2} s, NaN samples zeroed {4}" -f $Out, ($n / $rate), $Cycles, ($speech.Length / $rate), $nan)
