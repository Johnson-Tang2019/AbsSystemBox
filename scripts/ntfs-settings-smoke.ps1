# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/ntfs-new-'+[guid]::NewGuid().ToString('N'))
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
    Wait-For {$process.Refresh();$process.MainWindowHandle -ne 0} 'new settings window'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if([ProcessTokenProbe]::IsElevated($process)-or[ProcessTokenProbe]::IsAdministrator($process)){throw 'Expected restricted user'}
    (Find $main 'NavSettings').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'NtfsStatus').Current.Name -match '普通目录索引仍可用'} 'service absent fallback'
    $checks.Add('Restricted real WinUI shows missing/untrusted service and ordinary-index fallback')
    $input=Find $main 'NtfsRoot'
    $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('R:\')
    Click $main 'StartNtfs'
    Wait-For {$null-ne(Find $main 'NtfsApproval')} 'whole-volume approval'
    Click $main 'CloseButton'
    Wait-For {$null-eq(Find $main 'NtfsApproval')} 'cancelled consent'
    if(Test-Path (Join-Path $env:SYSBOX_TEST_INDEX_DIRECTORY 'metadata.db')){throw 'Cancelled consent created an index'}
    $checks.Add('Whole-volume consent can be cancelled; no metadata database or scan starts')
    $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Click $main 'InstallNtfsService'
    Wait-For {(Find $main 'NtfsStatus').Current.Name -match '请先填写'} 'empty grant input rejection'
    $checks.Add('Optional service setup rejects an empty volume before requesting administrator consent')
    $process.CloseMainWindow()|Out-Null
    if(-not $process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Exit failed'}
    @{isElevated=$false;isAdministrator=$false;checks=@($checks);volumeScanStarted=$false;serviceInstalled=$false}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $out 'ntfs-flow.json') -Encoding UTF8
    $checks
} catch {
    if($main){$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.AutomationId+': '+$_.Current.Name}|Set-Content (Join-Path $out 'failure-ui.txt') -Encoding UTF8}
    throw
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null}
}
