# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/desktop-'+[guid]::NewGuid().ToString('N'))
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
$checks=[Collections.Generic.List[string]]::new()
try {
    Wait-For {$process.Refresh();$process.MainWindowHandle -ne 0} 'main window'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if([ProcessTokenProbe]::IsElevated($process)-or[ProcessTokenProbe]::IsAdministrator($process)){throw 'Expected restricted user'}
    Wait-For {$null-ne(Find $main 'NavSettings')} 'settings navigation'
    (Find $main 'NavSettings').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {$null-ne(Find $main 'OpenQuickSearch')} 'desktop settings'
    Wait-For {(Find $main 'CleanupStatus').Current.Name -match '未启用|停用'} 'new backend cleanup read'
    if((Find $main 'DesktopStatus').Current.Name -match '不可用'){throw (Find $main 'DesktopStatus').Current.Name}
    (Find $main 'EnableFloatingBall').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Click $main 'SaveDesktop'
    Wait-For {$null-ne(Window-Title 'AbsSystemBox 悬浮球')} 'floating ball'
    $ball=Window-Title 'AbsSystemBox 悬浮球'
    Wait-For {$ball.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.Name -match '^\d+%$'}} 'real floating memory label'
    $checks.Add('Restricted desktop starts floating ball with real backend memory; native tray denial is explicitly reported and close-to-tray remains disabled')
    Click $main 'OpenQuickSearch'
    Wait-For {$null-ne(Window-Title 'AbsSystemBox 快捷搜索')} 'quick window'
    $quick=Window-Title 'AbsSystemBox 快捷搜索'
    (Find $quick 'QuickQuery').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('new-window-query')
    Wait-For {(Find $quick 'QuickStatus').Current.Name -match '尚未建立索引'} 'quick window uses remote index'
    $quick.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $checks.Add('Quick search opens, focuses input and reports genuine no-index state through CoreHost')
    (Find $main 'CleanupNames').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('WorkingSetFixture.exe')
    (Find $main 'CleanupEnabled').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $button=Find $main 'SaveCleanup'
    $scroll=$null
    if($button.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
    Click $main 'SaveCleanup'
    Wait-For {$null-ne(Find $main 'CleanupApproval')} 'explicit cleanup confirmation'
    Click $main 'CloseButton'
    if(Test-Path (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json')){
        $cleanup=Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json') -Raw|ConvertFrom-Json
        if($cleanup.Policy.Enabled){throw 'Cleanup was enabled despite cancellation'}
    }
    $checks.Add('New threshold/periodic cleanup UI requires explicit confirmation; cancel leaves cleanup disabled')
    $process.CloseMainWindow()|Out-Null
    if(-not $process.WaitForExit(15000)-or$process.ExitCode-ne 0){throw 'Desktop failed to exit'}
    $checks.Add('Main close closes floating/quick windows and stops its separate host')
    @{configuration='Debug';isElevated=$false;isAdministrator=$false;checks=@($checks)}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'desktop-flow.json') -Encoding UTF8
    $checks
} catch {
    if($main){$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.AutomationId+': '+$_.Current.Name}|Set-Content (Join-Path $out 'failure-ui.txt') -Encoding UTF8}
    throw
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null}
}
