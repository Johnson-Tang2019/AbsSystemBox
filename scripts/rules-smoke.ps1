# SPDX-License-Identifier: GPL-3.0-or-later
# Debug uses an isolated store. No test approves or edits the user's real rules.
param([switch] $Reopen)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$store = Join-Path $root 'artifacts/ui/m3-rule-store-final'
[IO.Directory]::CreateDirectory($store) | Out-Null
$env:SYSBOX_TEST_RULE_DIRECTORY = $store
$exe = Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
$process = Start-Process -FilePath $exe -PassThru
$checks = [Collections.Generic.List[string]]::new()
function Find-Id([string] $id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-For([scriptblock] $condition,[string] $description) {
    for($i=0;$i -lt 150;$i++) {
        $process.Refresh()
        if($process.HasExited) { throw "App exited: $description" }
        if(& $condition) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timeout: $description"
}
function Click([string] $id) { (Find-Id $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Text([string] $id,[string] $text) { (Find-Id $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function State { Get-Content -LiteralPath (Join-Path $store 'rule-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json }
function Dialog([string] $button) {
    Click 'ReviewRule'
    Wait-For { $null -ne (Find-Id 'RuleApprovalDialog') } 'approval dialog'
    Click $button
    Wait-For { $null -eq (Find-Id 'RuleApprovalDialog') } 'dialog closed'
}
try {
    Wait-For { $process.Refresh();$process.MainWindowHandle -ne 0 } 'window'
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For { $null -ne (Find-Id 'NavRules') } 'navigation'
    (Find-Id 'NavRules').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find-Id 'RuleYaml') } 'rules editor'
    Wait-For { (Find-Id 'RuleObservation').Current.Name -match 'v\d+' } 'real observer'
    $initial=State
    if($Reopen) {
        if($initial.Active.Revision -ne 3 -or $initial.Audit.Count -lt 4) { throw 'Persisted rules missing after restart' }
        $checks.Add('Reopened real application reads v3, proposals and audit')
    } else {
        if($initial.Active.Revision -ne 1) { throw 'Use a fresh isolated store for first run' }
        Set-Text 'RuleYaml' 'x: ['
        Click 'ValidateRule'
        Wait-For { (Find-Id 'RuleOutput').Current.Name -match 'YAML_SYNTAX' } 'invalid YAML error'
        if((State).Active.Revision -ne 1) { throw 'Invalid draft changed active rules' }
        $checks.Add('Invalid YAML shown with code; active v1 preserved')
        Click 'ReloadRule'
        $yaml=$initial.Active.Yaml.Replace('interval_seconds: 5','interval_seconds: 2').Replace('hold_seconds: 45','hold_seconds: 2').Replace('value: 12','value: 100').Replace('value: 80','value: 0')
        Set-Text 'RuleYaml' $yaml
        Click 'ValidateRule'
        Wait-For { (Find-Id 'RuleOutput').Current.Name -notmatch 'YAML_SYNTAX' } 'valid rule'
        Click 'SimulateRule'
        Wait-For { (Find-Id 'RuleOutput').Current.Name -match 'dry-run' } 'dry-run output'
        Click 'ProposeRule'
        Wait-For { (State).Proposals.Count -eq 1 } 'saved proposal'
        if((State).Active.Revision -ne 1) { throw 'Proposal applied without approval' }
        Dialog 'CloseButton'
        if((State).Active.Revision -ne 1) { throw 'Cancel changed active rules' }
        Dialog 'SecondaryButton'
        Wait-For { (State).Proposals[0].Status -eq 'Rejected' } 'rejection'
        Set-Text 'RuleYaml' $yaml
        Click 'ProposeRule'
        Wait-For { (State).Proposals.Count -eq 2 } 'second proposal'
        Dialog 'PrimaryButton'
        Wait-For { (State).Active.Revision -eq 2 } 'approved v2'
        Wait-For { (Find-Id 'RuleObservation').Current.Name -match 'Notified|Cooldown' } 'real notification-only rule'
        if(-not ((State).Audit | Where-Object Outcome -eq 'Notified')) { throw 'Notification audit missing' }
        $checks.Add('Dry-run, immutable proposal, cancel, reject and explicit approval verified')
        $checks.Add('Real memory pressure threshold produces application notification with audit; no cleanup')
        Click 'RollbackRule'
        Wait-For { (State).Proposals.Count -eq 3 } 'rollback proposal'
        if((State).Active.Revision -ne 2) { throw 'Rollback bypassed approval' }
        Dialog 'PrimaryButton'
        Wait-For { (State).Active.Revision -eq 3 } 'rollback v3'
        if((State).Active.Yaml -notmatch '(?m)^\s+value: 12\s*$') { throw 'Rollback did not restore threshold' }
        $checks.Add('Rollback creates reviewed v3 and restores original threshold')
        foreach($nav in @('NavMemory','NavDashboard','NavRules')) {
            (Find-Id $nav).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 250
        }
        Wait-For { (Find-Id 'ActiveRule').Current.Name -match 'v3' } 'rules after navigation'
        $checks.Add('Cached page navigation retains active rule and resumes display')
    }
    $process.CloseMainWindow() | Out-Null
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'App did not exit cleanly' }
    $checks.Add('Actual WinUI window exited with code 0')
    $name=if($Reopen){'rules-reopen.json'}else{'rules-flow.json'}
    @{ configuration='Debug'; isolatedStore=$true; demo=$false; checks=@($checks); revision=(State).Active.Revision; auditCount=(State).Audit.Count } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path (Split-Path $store -Parent) $name) -Encoding UTF8
    $checks
} finally {
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_RULE_DIRECTORY',$null,'Process')
    if(-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
