# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root ('artifacts/ui/cooling-' + [guid]::NewGuid().ToString('N'))
$env:SYSBOX_TEST_RULE_DIRECTORY = Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY = Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX = [guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($env:SYSBOX_TEST_RULE_DIRECTORY) | Out-Null
@{FloatingBall=$false;KeepInTray=$false;HotKey=0;Theme=2} | ConvertTo-Json | Set-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json') -Encoding UTF8
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/DockWindowProbe.cs')
$process = [RestrictedUserLauncher]::Launch((Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'), '')
function Find([string]$id) { $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) }
function Invoke-Button([string]$id) { (Find $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-For([scriptblock]$predicate, [string]$message) {
    for ($i=0; $i -lt 200; $i++) {
        if ($process.HasExited) { throw "Application exited: $message" }
        if (& $predicate) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timeout: $message"
}
try {
    if ([ProcessTokenProbe]::IsElevated($process) -or [ProcessTokenProbe]::IsAdministrator($process)) { throw 'Expected ordinary-user UI' }
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'window'
    $main = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    (Find 'NavCooling').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find 'CoolingDetect') } 'cooling page'
    if ((Find 'CoolingLastWrite').Current.Name -ne '尚未发送') { throw 'Unexpected automatic display write' }
    Invoke-Button 'CoolingDetect'
    Wait-For { (Find 'CoolingDetect').Current.IsEnabled -and (Find 'CoolingStatus').Current.Name -notmatch '尚未连接' } 'device and sensor discovery'
    $status = (Find 'CoolingStatus').Current.Name
    $sensorStatus = (Find 'CoolingSensorStatus').Current.Name
    if ($status -match '检测失败') { throw $status }
    if ($sensorStatus -match '不可用|超时|缺失') { throw $sensorStatus }
    $device = (Find 'CoolingDevice').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($device.Count -lt 1) { throw 'Expected connected compatible display' }
    $deviceName = $device[0].Current.Name
    # Only inspect GPU data. This flow never authorizes CPU/motherboard access or sends display frames.
    $temperature = Find 'CoolingTemperature'
    $temperature.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 200
    $items = @($main.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like '* · *' } | Select-Object -Unique)
    $temperature.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $workers = @(Get-Process SysBox.SensorHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/sensors/SysBox.SensorHost.exe') })
    if ($workers.Count -ne 1) { throw 'Expected exactly one sampling worker' }
    if ([ProcessTokenProbe]::IsAdministrator($workers[0])) { throw 'Default discovery elevated its worker' }
    Start-Sleep -Seconds 17
    if ($workers[0].HasExited) { throw 'Sampling session expired while choosing sensors' }
    Invoke-Button 'CoolingDetect'
    Wait-For { (Find 'CoolingDetect').Current.IsEnabled } 'second fresh sample'
    $bounds = [DockWindowProbe]::Bounds($process.MainWindowHandle)
    [DockWindowProbe]::ShowOnTop($process.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    $bitmap = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size) } finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $out 'cooling.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    (Find 'NavDashboard').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $workers[0].HasExited } 'worker exit after leaving cooling page'
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Fixture exit failed' }
    @{uiElevated=$false;workerElevated=$false;device=$deviceName;status=$status;sensorStatus=$sensorStatus;temperatureChoices=$items;workerSurvivedSelection=$true;workerStoppedOnLeave=$true;displayFramesWritten=0;privilegedConsentAutomated=$false;oldUiFlows='not rerun'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'report.json') -Encoding UTF8
    Join-Path $out 'report.json'
} finally {
    foreach ($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
