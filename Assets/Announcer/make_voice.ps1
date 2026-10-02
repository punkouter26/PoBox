# The ring announcer's voice: one short recording per phrase, made with the speech synthesiser that comes
# with Windows (no download, no service). Run with Windows PowerShell, which is the one that has it:
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File Assets\Announcer\make_voice.ps1
#
# A boxer's name is spoken from the file named after it in lower-case letters only ("LIL MATT" -> lilmatt.wav).
# Add a boxer: add a line to $names and run this again, then menu PoBox/Announcer/Rebuild Prefab.
param([string]$Voice = "Microsoft David Desktop", [int]$Rate = -2)
Add-Type -AssemblyName System.Speech
$out = Join-Path $PSScriptRoot "Voice"
New-Item -ItemType Directory -Force $out | Out-Null
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

$phrases = [ordered]@{
    red_corner  = "In the red corner!"
    blue_corner = "And in the blue corner!"
    round_1     = "Round one!"
    round_2     = "Round two!"
    round_3     = "Round three!"
    winner      = "And the winner is!"
    draw        = "It is a draw!"
    down        = "Down!"
}
$names = [ordered]@{
    matt    = "Matt!"
    zombie  = "Zombie!"
    trump   = "Trump!"
    grandma = "Grandma!"
    grandpa = "Grandpa!"
    nick    = "Nick!"
    lilmatt = "Little Matt!"
}

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice($Voice)
foreach ($set in @($phrases, $names)) {
    foreach ($key in $set.Keys) {
        $path = Join-Path $out "$key.wav"
        $synth.SetOutputToWaveFile($path, $format)
        # An announcer: slower than speech, lower, and leaning on every word.
        $ssml = "<speak version='1.0' xml:lang='en-US'><prosody rate='$Rate' pitch='low' volume='x-loud'><emphasis level='strong'>$($set[$key])</emphasis></prosody></speak>"
        try { $synth.SpeakSsml($ssml) } catch { $synth.Rate = $Rate; $synth.Speak($set[$key]) }
        $synth.SetOutputToNull()
        "{0,-14} {1,6:N0} bytes  {2}" -f "$key.wav", (Get-Item $path).Length, $set[$key]
    }
}
$synth.Dispose()
