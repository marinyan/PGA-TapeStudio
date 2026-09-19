$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /r:System.Windows.Forms.dll /r:"$PSScriptRoot\bin\pga.exe" /out:"$PSScriptRoot\bin\Tests.exe" "$PSScriptRoot\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& "$PSScriptRoot\bin\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
& (Join-Path $PSScriptRoot 'audio-engine-tests.ps1')
