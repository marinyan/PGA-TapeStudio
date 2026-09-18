$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $root 'RecordingLevelChecker.cs'))
# Replace only the WinMM imports in a throwaway build. No hardware is opened.
$pattern = '\[DllImport\("winmm.dll"[^\]]*\)\]\s*public static extern uint (\w+)\(([^;]*)\);'
$source = [regex]::Replace($source, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m)
    $name = $m.Groups[1].Value
    $parameters = $m.Groups[2].Value
    $body = if ($name.StartsWith('waveIn')) { 'throw new Exception("Unexpected microphone access");' }
    elseif ($name -eq 'waveOutOpen') { 'handle=FakeAudio.Open(id); return 0;' }
    elseif ($name -eq 'waveOutWrite') { 'FakeAudio.Headers.Add(header); return 0;' }
    elseif ($name -eq 'waveOutPause') { 'FakeAudio.Pauses++; return 0;' }
    elseif ($name -eq 'waveOutRestart') { 'FakeAudio.Restarts++; return 0;' }
    elseif ($name -eq 'waveOutSetVolume') { 'return FakeAudio.SetVolume(h,volume);' }
    elseif ($name -eq 'waveOutClose') { 'FakeAudio.Closed++; return 0;' }
    else { 'return 0;' }
    'public static uint '+$name+'('+$parameters+') { '+$body+' }'
})
$generated = Join-Path $root 'bin\AudioEngineUnderTest.cs'
[IO.File]::WriteAllText($generated, $source)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /main:RecordingLevelChecker.AudioEngineTests /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$root\bin\AudioEngineTests.exe" $generated "$root\AudioEngineTests.cs" "$root\AppSettings.cs" "$root\SignalComparison.cs" "$root\TapeRecognition.cs"
if ($LASTEXITCODE -ne 0) { throw 'Audio engine test build failed' }
& "$root\bin\AudioEngineTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Audio engine tests failed' }
