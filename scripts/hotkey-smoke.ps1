# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/hotkey-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($out)|Out-Null
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$exe=Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
$start=[Diagnostics.ProcessStartInfo]::new($exe);$start.UseShellExecute=$false;$process=[Diagnostics.Process]::Start($start)
function Windows {
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
}
function Find([System.Windows.Automation.AutomationElement]$parent,[string]$id){
    $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Click([System.Windows.Automation.AutomationElement]$parent,[string]$id){(Find $parent $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Wait-For([scriptblock]$predicate,[string]$message){
    for($i=0;$i -lt 200;$i++){if($process.HasExited){throw "Application exited: $message"};if(& $predicate){return};Start-Sleep -Milliseconds 100};throw "Timeout: $message"
}
function Window-Title([string]$title){Windows|Where-Object{$_.Current.Name -eq $title}|Select-Object -First 1}

Add-Type 'using System; using System.Runtime.InteropServices; public static class SysBoxHotkeyInput { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra); }'
try {
    Wait-For {$process.Refresh();$process.MainWindowHandle-ne0} 'main'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    (Find $main 'NavSettings').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'DesktopStatus').Current.Name -notmatch '尚未启动'} 'desktop initialization'
    $trayStatus=(Find $main 'DesktopStatus').Current.Name
    $combo=Find $main 'DesktopHotKey'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Ctrl + Alt + Q'))
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Click $main 'SaveDesktop'
    Wait-For {(Test-Path (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json'))} 'saved hotkey'
    $preferences=Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json') -Raw|ConvertFrom-Json
    if($preferences.HotKey-ne2){throw 'Hotkey was occupied or did not save'}
    $handle=[IntPtr]$main.Current.NativeWindowHandle
    [SysBoxHotkeyInput]::SetForegroundWindow($handle)|Out-Null
    if((Find $main 'DesktopStatus').Current.Name -match '占用|未生效'){throw 'Refusing to send an unregistered shortcut'}
    try{foreach($key in @(0x11,0x12,0x51)){[SysBoxHotkeyInput]::keybd_event([byte]$key,0,0,[UIntPtr]::Zero)}}
    finally{foreach($key in @(0x51,0x12,0x11)){[SysBoxHotkeyInput]::keybd_event([byte]$key,0,2,[UIntPtr]::Zero)}}
    Wait-For {$null-ne(Window-Title 'AbsSystemBox 快捷搜索')} 'real registered global hotkey'
    (Window-Title 'AbsSystemBox 快捷搜索').GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    @{trayStatus=$trayStatus;isElevated=[ProcessTokenProbe]::IsElevated($process);checks=@('Saved Ctrl+Alt+Q registration; actual keyboard input opened quick search')}|ConvertTo-Json|Set-Content (Join-Path $out 'hotkey-flow.json') -Encoding UTF8
    $process.CloseMainWindow()|Out-Null
    if(-not $process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Hotkey flow exit failed'}
    Get-Content (Join-Path $out 'hotkey-flow.json')
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null}
}
