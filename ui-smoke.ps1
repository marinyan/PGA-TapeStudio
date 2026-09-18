param([switch]$SampleWaveforms)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'bin\RecordingLevelChecker.exe')) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$testSettings = Join-Path $PSScriptRoot ('bin\ui-test-' + [Guid]::NewGuid().ToString('N') + '.xml')
$form = New-Object RecordingLevelChecker.MainForm($testSettings)
try {
    $form.ShowInTaskbar = $false
    $form.Opacity = 0
    $form.Show()
    $form.PerformLayout()
    if ($SampleWaveforms) {
        $view = $form.Controls[0].Controls | Where-Object { $_ -is [RecordingLevelChecker.WaveView] }
        $sent = New-Object 'Int16[]' 4800
        $received = New-Object 'Int16[]' 5760
        for ($i = 0; $i -lt $sent.Length; $i++) {
            $sent[$i] = [int16](18000 * [Math]::Sin(2 * [Math]::PI * $i / 480))
            $received[$i + 480] = [int16][Math]::Max(-8500, [Math]::Min(8500, $sent[$i]))
        }
        $view.Playback = $sent
        $view.Samples = $received
        $view.Aligned = $true
        $view.DelaySamples = 480
        $view.Center = 2400
    }
    [System.Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
        $name = if ($SampleWaveforms) { 'bin\ui-waveforms-preview.png' } else { 'bin\ui-preview.png' }
        $bitmap.Save((Join-Path $PSScriptRoot $name))
    } finally { $bitmap.Dispose() }
    Write-Output 'PASS: form construction, audio device enumeration and bitmap rendering'
    $flags = [System.Reflection.BindingFlags]'Instance,NonPublic'
    $whole = $form.GetType().GetField('wholeFile', $flags).GetValue($form)
    $whole.Checked = $false
    if ($form.GetType().GetField('pitch', $flags)) { throw 'Obsolete pitch control remains' }
    $model = $form.GetType().GetField('recognitionModel', $flags).GetValue($form)
    $model.SelectedIndex = 1
    $volume = $form.GetType().GetField('monitorVolumeSlider', $flags).GetValue($form)
    $volume.Value = 37
    $data = New-Object System.Windows.Forms.DataObject
    $droppedPath = Join-Path $PSScriptRoot 'Tests.cs'
    $data.SetData([System.Windows.Forms.DataFormats]::FileDrop, [string[]]@($droppedPath))
    $drag = New-Object System.Windows.Forms.DragEventArgs($data, 0, 0, 0, [System.Windows.Forms.DragDropEffects]::Copy, [System.Windows.Forms.DragDropEffects]::None)
    $enter = [System.Windows.Forms.Control].GetMethod('OnDragEnter', $flags)
    $drop = [System.Windows.Forms.Control].GetMethod('OnDragDrop', $flags)
    $enter.Invoke($form, @($drag.PSObject.BaseObject)) | Out-Null
    if ($drag.Effect -ne [System.Windows.Forms.DragDropEffects]::Copy) { throw 'Drop not accepted' }
    $drop.Invoke($form, @($drag.PSObject.BaseObject)) | Out-Null
    $loaded = [RecordingLevelChecker.AppSettings]::Load($testSettings)
    if ($loaded.LastFile -ne $droppedPath -or $loaded.WholeFile) { throw 'UI did not persist selection' }
    if ($loaded.RecognitionModel -ne 'PC_6001') { throw 'Recognition model not saved' }
    if ($loaded.MonitorVolumePercent -ne 37) { throw 'Monitor volume not saved' }
    $second = New-Object RecordingLevelChecker.MainForm($testSettings)
    try {
        $restoredFile = $second.GetType().GetField('file', $flags).GetValue($second)
        $restoredWhole = $second.GetType().GetField('wholeFile', $flags).GetValue($second)
        if ($restoredFile.Text -ne $droppedPath -or $restoredWhole.Checked) { throw 'UI did not restore settings' }
        $restoredModel = $second.GetType().GetField('recognitionModel', $flags).GetValue($second)
        if ($restoredModel.Text -ne 'PC_6001') { throw 'Recognition model not restored' }
        $restoredVolume = $second.GetType().GetField('monitorVolumeSlider', $flags).GetValue($second)
        if ($restoredVolume.Value -ne 37) { throw 'Monitor volume not restored' }
    } finally { $second.Dispose() }
    $busyMethod = $form.GetType().GetMethod('SetBusy', $flags)
    $busyMethod.Invoke($form, @($true)) | Out-Null
    if (!$volume.Enabled) { throw 'Live monitor volume disabled while busy' }
    $volume.Value = 62
    $liveVolume = $form.GetType().GetField('monitorLevel', $flags).GetValue($form)
    if ($liveVolume.Percent -ne 62) { throw 'Live monitor volume not forwarded' }
    $enter.Invoke($form, @($drag.PSObject.BaseObject)) | Out-Null
    if ($drag.Effect -ne [System.Windows.Forms.DragDropEffects]::None) { throw 'Busy UI accepted a drop' }
    $busyMethod.Invoke($form, @($false)) | Out-Null
    Write-Output 'PASS: UI drop selection, autosave, restart restore and busy drop rejection (no playback)'
} finally {
    $form.Dispose()
    if (Test-Path -LiteralPath $testSettings) { Remove-Item -LiteralPath $testSettings }
}
