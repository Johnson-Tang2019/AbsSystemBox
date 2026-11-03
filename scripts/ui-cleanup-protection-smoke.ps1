# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/protection-new-'+[guid]::NewGuid().ToString('N'))
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($env:SYSBOX_TEST_RULE_DIRECTORY)|Out-Null
$fixture=Join-Path $out 'size-fixture'
[IO.Directory]::CreateDirectory($fixture)|Out-Null
foreach($item in @(@('megabytes.bin',1200000),@('kilobytes.bin',1500))){
    $file=[IO.File]::Create((Join-Path $fixture $item[0]));try{$file.SetLength($item[1])}finally{$file.Dispose()}
}
@{Policy=@{Enabled=$false;AllowedProcesses=@('old.exe')};Audit=@();Runs=@()}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json') -Encoding UTF8
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$process=[RestrictedUserLauncher]::Launch((Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'),'')
function Find([System.Windows.Automation.AutomationElement]$parent,[string]$id){$parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Click([System.Windows.Automation.AutomationElement]$parent,[string]$id){(Find $parent $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Wait-For([scriptblock]$predicate,[string]$message){
    for($i=0;$i-lt200;$i++){if($process.HasExited){throw "Application exited: $message"};if(& $predicate){return};Start-Sleep -Milliseconds 100};throw "Timeout: $message"
}
function Names($parent){@($parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name})}
try{
    if([ProcessTokenProbe]::IsElevated($process)-or[ProcessTokenProbe]::IsAdministrator($process)){throw 'Expected ordinary-user UI'}
    Wait-For {$process.Refresh();$process.MainWindowHandle-ne0} 'isolated window'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For {$null-ne(Find $main 'NavMemory')} 'navigation'
    (Find $main 'NavMemory').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {(Find $main 'CleanupStatus').Current.Name-match'旧白名单范围'} 'legacy policy scope notice'
    $names=Find $main 'CleanupNames'
    $names.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
    $enabled=(Find $main 'CleanupEnabled').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $enabled.Toggle()
    Click $main 'SaveCleanup'
    Wait-For {$null-ne(Find $main 'CleanupApproval')} 'empty exclusion list reaches approval'
    $dialog=Find $main 'CleanupApproval'
    if(-not((Names $dialog)-match'当前用户未受保护的后台进程')){throw 'Approval omits new scope'}
    $cancel=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'取消'))
    $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For {$null-eq(Find $main 'CleanupApproval')} 'approval cancellation'
    $stored=Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json') -Raw|ConvertFrom-Json
    if($stored.Policy.Enabled-or$stored.Policy.AllowedProcesses[0]-ne'old.exe'){throw 'Cancellation changed old policy'}
    $enabled.Toggle()
    $names.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Browser.EXE')
    Click $main 'SaveCleanup'
    Wait-For {(Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json') -Raw|ConvertFrom-Json).Policy.ProcessScope-eq1} 'new scope save'
    $stored=Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'cleanup.json') -Raw|ConvertFrom-Json
    if($stored.Policy.Enabled-or$stored.Policy.ExcludedProcesses[0]-ne'browser.exe'){throw 'Disabled exclusion policy mismatch'}
    Click $main 'PclCleanupPreset'
    $checkboxes=$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::CheckBox))
    $selected=@($checkboxes|Where-Object{$_.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState-eq1}|ForEach-Object{$_.Current.Name})
    if($selected-notcontains'完整待机列表（可能短暂停顿）'-or$selected-contains'系统文件缓存'){throw 'PCL selection mismatch'}
    if((Find $main 'SystemCleanupResult').Current.Name-notmatch'尚未执行'){throw 'Preset unexpectedly executes cleanup'}
    Click $main 'FluentCleanupPreset'
    $standby=$checkboxes|Where-Object{$_.Current.Name-eq'完整待机列表（可能短暂停顿）'}
    if($standby.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState-ne0){throw 'Default preset did not restore guarded selection'}
    (Find $main 'NavStorage').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For {$null-ne(Find $main 'StoragePath')} 'storage page'
    (Find $main 'StoragePath').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
    Click $main 'AnalyzeStorage'
    Wait-For {(Find $main 'StorageSummary').Current.Name-match'MB 逻辑大小'} 'formatted actual fixture summary'
    $childNames=Names (Find $main 'StorageChildren')
    if(-not($childNames-match'KB 逻辑大小')-or-not($childNames-match'MB 逻辑大小')){throw 'Child sizes lack KB/MB'}
    $process.CloseMainWindow()|Out-Null
    if(-not$process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Fixture exit failed'}
    @{isElevated=$false;emptyExclusionsReachApproval=$true;cancellationPreservesLegacy=$true;disabledExclusionsPersist=$true;pclPresetSelectionOnly=$true;storageFixtureBytes=1201500;storageKbMbRuntime=$true;systemCleanupExecuted=$false;volumeScanned=$false}|ConvertTo-Json|Set-Content (Join-Path $out 'protection-flow.json') -Encoding UTF8
    Join-Path $out 'protection-flow.json'
}catch{
    if($main){Names $main|Set-Content (Join-Path $out 'failure-ui.txt') -Encoding UTF8};throw
}finally{
    if($main){
        $pending=Find $main 'CleanupApproval'
        if($pending){
            $close=$pending.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'取消'))
            if($close){$close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
        }
    }
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not$process.HasExited){$process.CloseMainWindow()|Out-Null}
}
