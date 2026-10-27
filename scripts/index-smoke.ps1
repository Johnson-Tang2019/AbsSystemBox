# SPDX-License-Identifier: GPL-3.0-or-later
# This test scans only a newly-created fixture and uses isolated Debug state.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root ('artifacts/ui/m4-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $out 'sample'
[IO.Directory]::CreateDirectory((Join-Path $fixture 'child')) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'child/model.pt'), 'fixture-data')
[IO.File]::WriteAllText((Join-Path $fixture 'readme.txt'), 'fixture')
$env:SYSBOX_TEST_RULE_DIRECTORY = Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY = Join-Path $out 'index'
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$exe = Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
$process = [RestrictedUserLauncher]::Launch($exe, '')
$checks = [Collections.Generic.List[string]]::new()
function Find-Id([string] $id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-For([scriptblock] $condition,[string] $description) {
    for ($i=0; $i -lt 150; $i++) {
        $process.Refresh(); if ($process.HasExited) { throw "Application exited: $description" }
        if (& $condition) { return }; Start-Sleep -Milliseconds 100
    }; throw "Timeout: $description"
}
function Click([string] $id) { (Find-Id $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Status { (Find-Id 'IndexStatus').Current.Name }
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'window'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ([ProcessTokenProbe]::IsElevated($process) -or [ProcessTokenProbe]::IsAdministrator($process)) { throw 'Expected non-administrator' }
    Wait-For { $null -ne (Find-Id 'NavSearch') } 'navigation'
    (Find-Id 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find-Id 'IndexPath') } 'index page'
    (Find-Id 'IndexPath').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
    Click 'StartIndex'
    Wait-For { (Status) -match '扫描完成.*2 文件' } 'scan complete'
    $checks.Add('Restricted non-administrator WinUI scans isolated directory: 2 files, 2 directories')
    [IO.File]::Move((Join-Path $fixture 'readme.txt'),(Join-Path $fixture 'renamed.txt'))
    Click 'StartIndex'
    Wait-For { (Status) -match '扫描完成.*2 文件' } 'rescan complete'
    (Find-Id 'SearchQuery').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('renamed')
    Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'renamed.txt')
        $null -ne $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
    } 'updated preview'
    $checks.Add('Manual rename and rescan update actual search ListView')
    Click 'ClearIndex'; Wait-For { $null -ne (Find-Id 'CloseButton') } 'clear confirmation'
    Click 'CloseButton'
    Wait-For { $null -eq (Find-Id 'CloseButton') } 'cancelled confirmation closed'
    Start-Sleep -Milliseconds 300
    if (-not (Test-Path (Join-Path $env:SYSBOX_TEST_INDEX_DIRECTORY 'metadata.db'))) { throw 'Cancel lost database' }
    Click 'ClearIndex'; Wait-For { $null -ne (Find-Id 'PrimaryButton') } 'clear confirmation again'
    Click 'PrimaryButton'; Wait-For { (Status) -match '索引缓存已清除' } 'cache cleared'
    if ([IO.File]::ReadAllText((Join-Path $fixture 'child/model.pt')) -ne 'fixture-data') { throw 'Source data changed' }
    $checks.Add('Clear confirmation cancel and approve both verified; source file unchanged')
    (Find-Id 'NavMemory').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    (Find-Id 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { (Status) -match '尚未建立索引' } 'empty state after navigation'
    $checks.Add('Cached page navigation reloads empty index state')
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Application did not close cleanly' }
    $checks.Add('Actual application window exited with code 0')
    @{ configuration='Debug'; isolatedStore=$true; isElevated=$false; isAdministrator=$false; checks=@($checks) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'index-flow.json') -Encoding UTF8
    $checks; Write-Output (Join-Path $out 'index-flow.json')
} finally {
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_RULE_DIRECTORY',$null,'Process')
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_INDEX_DIRECTORY',$null,'Process')
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
