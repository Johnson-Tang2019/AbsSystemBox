# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/host-sharing-'+[guid]::NewGuid().ToString('N'))
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

$fixture=Join-Path $out 'fixture'
[IO.Directory]::CreateDirectory($fixture)|Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'host-shared.txt'),'pipeline-data')
$cli=Join-Path $root 'src/SysBox.Cli/bin/Debug/net10.0-windows/sysbox.exe'
function Invoke-SysBoxCli([string[]]$words){$text=& $cli @words --json;if($LASTEXITCODE-ne0){throw $text};$text|ConvertFrom-Json}
try {
    Wait-For {$process.Refresh();$process.MainWindowHandle -ne 0} 'main'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    (Find $main 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {$null-ne(Find $main 'IndexPath')} 'new remote scan entry'
    (Find $main 'IndexPath').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
    Click $main 'StartIndex'
    Wait-For {(Find $main 'IndexStatus').Current.Name -match '扫描完成.*1 文件'} 'separate host publishes job'
    $search=Invoke-SysBoxCli -words @('search','host-shared');if($search.data.hits.Count-ne1){throw 'CLI did not share UI index'}
    $storage=Invoke-SysBoxCli -words @('storage','children',$fixture);if($storage.data.summary.logicalBytes-ne13){throw 'CLI storage did not share UI snapshot'}
    $yaml=Join-Path $root 'src/SysBox.Rules/default.yaml'
    $validation=Invoke-SysBoxCli -words @('rules','validate',$yaml);if(-not $validation.data.isValid){throw 'Rule DTO transport failed'}
    $simulation=Invoke-SysBoxCli -words @('rules','simulate',$yaml);if($simulation.data.actuallyExecuted-ne0){throw 'Simulation is not read-only'}
    $proposal=Invoke-SysBoxCli -words @('rules','propose',$yaml);$history=Invoke-SysBoxCli -words @('rules','history')
    if($proposal.data.status-ne'Pending'-or$history.data.activeRevision-ne1-or$history.data.pending.Count-ne1){throw 'Proposal changed active rules'}
    $checks=@('Trusted restricted UI creates a one-file scope through IPC; CLI search and storage see the same host snapshot','New CLI rule validate/simulate/propose commands serialize real DTOs; proposal remains pending and active revision stays 1')
    @{checks=$checks}|ConvertTo-Json|Set-Content (Join-Path $out 'host-sharing-flow.json') -Encoding UTF8
    $process.CloseMainWindow()|Out-Null
    if(-not $process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Host-sharing exit failed'}
    $checks
} finally {
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null}
}
