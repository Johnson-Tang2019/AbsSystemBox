# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/export-delta-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($out)|Out-Null
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$exe=Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
$process=[RestrictedUserLauncher]::Launch($exe,'')
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

try {
    Wait-For {$process.Refresh();$process.MainWindowHandle -ne 0} 'main'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    (Find $main 'NavDiagnostics').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'HostHealth').Current.Name -match '后台在线'} 'host health'
    $before=@([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.NativeWindowHandle})
    Click $main 'ExportDiagnostics'
    $script:save=$null
    Wait-For {
        $script:save=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'另存为'))
        if($null-eq$script:save){$script:save=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.NativeWindowHandle -notin $before -and $_.Current.Name -match '保存|Save|另存'}|Select-Object -First 1}
        $null-ne$script:save
    } 'new user file picker'
    $cancel=$save.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'取消'))
    if($null-eq$cancel){throw 'Cancel not found'}
    Add-Type 'using System; using System.Runtime.InteropServices; public static class SysBoxPickerCancel { [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessageW(IntPtr window,uint message,IntPtr w,IntPtr l); }'
    if(-not [SysBoxPickerCancel]::PostMessageW([IntPtr]$save.Current.NativeWindowHandle,273,[IntPtr]2,[IntPtr]::Zero)){throw 'Unable to post cancel'}
    Wait-For {(Find $main 'ExportStatus').Current.Name -match '取消'} 'export cancellation'
    @{checks=@('Real host health shown in UI; real Save picker appears and cancel leaves no exported file')}|ConvertTo-Json|Set-Content (Join-Path $out 'export-flow.json') -Encoding UTF8
    $process.CloseMainWindow()|Out-Null
    if(-not $process.WaitForExit(15000)-or$process.ExitCode-ne 0){throw 'Exit failed'}
    Get-Content (Join-Path $out 'export-flow.json')
} catch {
    if($main){$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.AutomationId+': '+$_.Current.Name}|Set-Content (Join-Path $out 'failure-ui.txt') -Encoding UTF8}
    throw
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null}
}
