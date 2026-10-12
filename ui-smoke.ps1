param([switch]$SampleWaveforms)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'bin\pga.exe')) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$testSettings = Join-Path $PSScriptRoot ('bin\ui-test-' + [Guid]::NewGuid().ToString('N') + '.xml')
$form = New-Object RecordingLevelChecker.MainForm($testSettings)
try {
    $form.ShowInTaskbar = $false
    $form.Opacity = 0
    $form.Show()
    $form.PerformLayout()
    if ($SampleWaveforms) {
        $view = $form.GetType().GetField('wave', [System.Reflection.BindingFlags]'Instance,NonPublic').GetValue($form)
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
    $tabs = $form.Controls.Find('workspaceTabs', $true)[0]
    if ($tabs.TabPages.Count -ne 3) { throw 'Workspace tabs missing' }
    foreach ($tabIndex in @(1, 2)) {
        $tabs.SelectedIndex = $tabIndex
        [System.Windows.Forms.Application]::DoEvents()
        $pageBitmap = New-Object System.Drawing.Bitmap($form.Width, $form.Height)
        try {
            $form.DrawToBitmap($pageBitmap, (New-Object System.Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
            $pageBitmap.Save((Join-Path $PSScriptRoot ('bin\ui-tab-' + $tabIndex + '.png')))
        } finally { $pageBitmap.Dispose() }
    }
    $tabs.SelectedIndex = 0
    $flags = [System.Reflection.BindingFlags]'Instance,NonPublic'
    $picker = $form.GetType().GetField('file', $flags).GetValue($form)
    $remember = $form.GetType().GetMethod('RecordAudioUse', $flags)
    $remember.Invoke($form, @([string](Join-Path $PSScriptRoot 'README.md'))) | Out-Null
    $remember.Invoke($form, @([string](Join-Path $PSScriptRoot 'Tests.cs'))) | Out-Null
    $picker.SelectedIndex = 0
    $picker.CycleHistory(-120)
    if (!$picker.Text.EndsWith('README.md')) { throw 'Wheel did not select history' }
    $picker.CycleHistory(120)
    if (!$picker.Text.EndsWith('Tests.cs')) { throw 'Reverse wheel did not select history' }
    $whole = $form.GetType().GetField('wholeFile', $flags).GetValue($form)
    $whole.Checked = $false
    if ($form.GetType().GetField('pitch', $flags)) { throw 'Obsolete pitch control remains' }
    $model = $form.GetType().GetField('recognitionModel', $flags).GetValue($form)
    $model.SelectedIndex = 1
    $correction = $form.GetType().GetField('correctionModel', $flags).GetValue($form)
    $correction.SelectedItem = 'X1'
    $export = $form.GetType().GetField('exportAudio', $flags).GetValue($form)
    $volume = $form.GetType().GetField('monitorVolumeSlider', $flags).GetValue($form)
    $volume.Value = 37
    $autoGain = $form.GetType().GetField('autoGain', $flags).GetValue($form)
    $manualGain = $form.GetType().GetField('gain', $flags).GetValue($form)
    $autoGain.Checked = $true
    $phase = $form.GetType().GetField('invertPhase', $flags).GetValue($form)
    $phase.Checked = $true
    if ($manualGain.Enabled) { throw 'Manual gain enabled during auto gain' }
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
    if ($loaded.CorrectionModel -ne 'X1') { throw 'Correction profile not saved' }
    if ($loaded.MonitorVolumePercent -ne 37) { throw 'Monitor volume not saved' }
    if (!$loaded.AutoGain) { throw 'Automatic gain not saved' }
    if (!$loaded.InvertPhase) { throw 'Phase inversion not saved' }
    $second = New-Object RecordingLevelChecker.MainForm($testSettings)
    try {
        $restoredFile = $second.GetType().GetField('file', $flags).GetValue($second)
        $restoredWhole = $second.GetType().GetField('wholeFile', $flags).GetValue($second)
        if ($restoredFile.Text -ne $droppedPath -or $restoredWhole.Checked) { throw 'UI did not restore settings' }
        $restoredModel = $second.GetType().GetField('recognitionModel', $flags).GetValue($second)
        if ($restoredModel.Text -ne 'PC_6001') { throw 'Recognition model not restored' }
        if ($second.GetType().GetField('correctionModel', $flags).GetValue($second).Text -ne 'X1') { throw 'Correction profile not restored' }
        $restoredVolume = $second.GetType().GetField('monitorVolumeSlider', $flags).GetValue($second)
        if ($restoredVolume.Value -ne 37) { throw 'Monitor volume not restored' }
        $restoredAuto = $second.GetType().GetField('autoGain', $flags).GetValue($second)
        $restoredGain = $second.GetType().GetField('gain', $flags).GetValue($second)
        if (!$restoredAuto.Checked -or $restoredGain.Enabled) { throw 'Automatic gain not restored' }
        if (!$second.GetType().GetField('invertPhase', $flags).GetValue($second).Checked) { throw 'Phase inversion not restored' }
    } finally { $second.Dispose() }
    $busyMethod = $form.GetType().GetMethod('SetBusy', $flags)
    $busyMethod.Invoke($form, @($true)) | Out-Null
    if ($correction.Enabled -or $export.Enabled) { throw 'Correction/export controls remain enabled while busy' }
    $transport = $form.GetType().GetField('playOnly', $flags).GetValue($form)
    if ($transport.Enabled) { throw 'Transport enabled during preparation' }
    $pauseField = $form.GetType().GetField('pauseControl', $flags)
    $pauseState = New-Object RecordingLevelChecker.MeasurementPause
    $pauseField.SetValue($form, $pauseState)
    $refreshTransport = $form.GetType().GetMethod('RefreshTransport', $flags)
    $refreshTransport.Invoke($form, @()) | Out-Null
    if (!$transport.Enabled -or !$transport.Text.Contains('一時停止')) { throw 'Transport did not become pause' }
    $transport.PerformClick()
    if (!$pauseState.Requested -or !$transport.Text.Contains('再開')) { throw 'Transport did not become resume' }
    $transport.PerformClick()
    if ($pauseState.Requested -or !$transport.Text.Contains('一時停止')) { throw 'Transport did not resume' }
    $pauseField.SetValue($form, $null)
    $refreshTransport.Invoke($form, @()) | Out-Null
    if ($transport.Enabled) { throw 'Transport enabled during analysis' }
    $beforeWheel = $picker.Text
    $picker.CycleHistory(-120)
    if ($picker.Enabled -or $picker.Text -ne $beforeWheel) { throw 'Busy history selection changed' }
    if (!$volume.Enabled) { throw 'Live monitor volume disabled while busy' }
    if ($autoGain.Enabled -or $manualGain.Enabled) { throw 'Gain editable while busy' }
    if ($phase.Enabled) { throw 'Phase editable while busy' }
    $volume.Value = 62
    $liveVolume = $form.GetType().GetField('monitorLevel', $flags).GetValue($form)
    if ($liveVolume.Percent -ne 62) { throw 'Live monitor volume not forwarded' }
    $enter.Invoke($form, @($drag.PSObject.BaseObject)) | Out-Null
    if ($drag.Effect -ne [System.Windows.Forms.DragDropEffects]::None) { throw 'Busy UI accepted a drop' }
    $busyMethod.Invoke($form, @($false)) | Out-Null
    if (!$transport.Enabled -or !$transport.Text.Contains('再生')) { throw 'Idle transport not restored' }
    if ($manualGain.Enabled) { throw 'Automatic mode did not keep manual gain disabled' }
    $autoGain.Checked = $false
    if (!$manualGain.Enabled) { throw 'Manual gain not restored after auto mode' }
    Write-Output 'PASS: UI drop selection, autosave, restart restore and busy drop rejection (no playback)'
} finally {
    $form.Dispose()
    if (Test-Path -LiteralPath $testSettings) { Remove-Item -LiteralPath $testSettings }
}
