$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'bin') | Out-Null
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$root\bin\RecordingLevelChecker.exe" "$root\RecordingLevelChecker.cs" "$root\SignalComparison.cs" "$root\AppSettings.cs" "$root\TapeRecognition.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
