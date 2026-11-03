# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/revision-new-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($out)|Out-Null
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$exe=Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
[IO.Directory]::CreateDirectory($env:SYSBOX_TEST_RULE_DIRECTORY)|Out-Null
@{FloatingBall=$true;KeepInTray=$false;HotKey=0;Theme=2;BallX=300;BallY=200}|ConvertTo-Json|Set-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json') -Encoding UTF8
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


$checks=[Collections.Generic.List[string]]::new()
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class RevisionWindowProbe{[DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);[StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}[DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr w,out Rect r);[DllImport("user32.dll")]public static extern bool GetClientRect(IntPtr w,out Rect r);[DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr w);}'
try {
    Wait-For {$process.Refresh();$process.MainWindowHandle-ne0} 'new isolated window'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For {(Find $main 'DashboardIndex').Current.Name-match'尚未建立|已索引'} 'real overview index status'
    Wait-For {(Find $main 'DashboardStorage').Current.Name-match'GiB'} 'real volume capacity'
    if(Find $main 'RefreshStatus'){throw 'Overview still has refresh controls'}
    $checks.Add('Overview has genuine index/capacity status and no manual refresh panel')
    Wait-For {$null-ne(Window-Title 'AbsSystemBox 悬浮球')} 'floating window'
    $ball=Window-Title 'AbsSystemBox 悬浮球';$handle=[IntPtr]$ball.Current.NativeWindowHandle
    [RevisionWindowProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))|Out-Null
    $rect=[RevisionWindowProbe+Rect]::new();$client=[RevisionWindowProbe+Rect]::new()
    [RevisionWindowProbe]::GetWindowRect($handle,[ref]$rect)|Out-Null;[RevisionWindowProbe]::GetClientRect($handle,[ref]$client)|Out-Null
    $dpi=[RevisionWindowProbe]::GetDpiForWindow($handle);$expected=[int][Math]::Ceiling(64*$dpi/96.0)
    if($rect.Right-$rect.Left-ne$expected-or$rect.Bottom-$rect.Top-ne$expected-or$client.Right-ne$expected-or$client.Bottom-ne$expected){throw "Floating geometry mismatch: window $($rect.Right-$rect.Left)x$($rect.Bottom-$rect.Top), client $($client.Right)x$($client.Bottom), DPI $dpi"}
    Wait-For {$ball.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.Name-match'^\d+%$'}} 'floating real percent'
    $label=$ball.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.Name-match'^\d+%$'}|Select-Object -First 1
    $bounds=$label.Current.BoundingRectangle
    if($bounds.Left-lt$rect.Left-or$bounds.Right-gt$rect.Right-or$bounds.Top-lt$rect.Top-or$bounds.Bottom-gt$rect.Bottom){throw 'Floating percentage is clipped'}
    $checks.Add('Actual floating HWND/client are exactly 64 DIP; real percentage bounds fully fit')
    (Find $main 'NavMemory').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'ProcessCoverage').Current.Name-match'工作集'} 'working-set order'
    if(Find $main 'RefreshStatus'){throw 'Memory refresh panel is still present'}
    $sort=Find $main 'ProcessSort';$sort.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item=$main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'私有提交，从高到低'))
    if(-not$item){$item=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'私有提交，从高到低'))}
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'ProcessCoverage').Current.Name-match'私有提交'} 'private-commit order'
    Wait-For {$null-ne(Find $main 'CleanupEnabled')} 'relocated cleanup controls'
    if(((Find $main 'CleanupEnabled').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState-eq1){throw 'Fixture cleanup unexpectedly enabled'}
    $checks.Add('Memory automatically refreshes, supports commit sorting and includes disabled cleanup configuration')
    (Find $main 'NavDiagnostics').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'BatterySourceStatus').Current.Name-match'电池：无|电池健康度'} 'battery health language'
    $checks.Add('Battery diagnostics show health/no-battery wording without raw enum or SDK attribution')
    $process.CloseMainWindow()|Out-Null
    if(-not$process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Fixture exit failed'}
    @{isElevated=$false;isAdministrator=$false;checks=@($checks);ballDpi=$dpi;ballPixelSize=$expected;systemCleanupExecuted=$false;volumeScanned=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'revision-flow.json') -Encoding UTF8
    $checks
} catch {
    if($main){$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.AutomationId+': '+$_.Current.Name}|Set-Content (Join-Path $out 'failure-ui.txt') -Encoding UTF8}
    throw
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not$process.HasExited){$process.CloseMainWindow()|Out-Null}
}
