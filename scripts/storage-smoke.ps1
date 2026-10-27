# SPDX-License-Identifier: GPL-3.0-or-later
param([ValidateSet('Debug','Release')][string] $Configuration='Debug',[switch] $ShellOnly)
$ErrorActionPreference = 'Stop'
if ($Configuration -eq 'Release' -and -not $ShellOnly) { throw 'Release validation must use ShellOnly; test directory override is Debug-only' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root ('artifacts/ui/m7-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $out 'sample'
$deep = Join-Path $fixture 'one/two/three'
[IO.Directory]::CreateDirectory($deep) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $fixture 'empty')) | Out-Null
[IO.File]::WriteAllBytes((Join-Path $fixture 'root.txt'),[byte[]]::new(10))
[IO.File]::WriteAllBytes((Join-Path $fixture 'one/one.txt'),[byte[]]::new(20))
[IO.File]::WriteAllBytes((Join-Path $fixture 'one/two/two.txt'),[byte[]]::new(30))
[IO.File]::WriteAllBytes((Join-Path $deep 'three.txt'),[byte[]]::new(40))
if ($Configuration -eq 'Debug') {
    $env:SYSBOX_TEST_RULE_DIRECTORY = Join-Path $out 'rules'
    $env:SYSBOX_TEST_INDEX_DIRECTORY = Join-Path $out 'index'
}
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$exe = Join-Path $root "src/SysBox.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe"
$process = [RestrictedUserLauncher]::Launch($exe, '')
$checks = [Collections.Generic.List[string]]::new()
function Find-Id([string] $id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-For([scriptblock] $condition,[string] $description) {
    for ($i=0; $i -lt 200; $i++) {
        $process.Refresh(); if ($process.HasExited) { throw "Application exited: $description" }
        if (& $condition) { return }; Start-Sleep -Milliseconds 100
    }; throw "Timeout: $description"
}
function Click([string] $id) { (Find-Id $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Summary { (Find-Id 'StorageSummary').Current.Name }
function Set-Path([string] $text) { (Find-Id 'StoragePath').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'window'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ([ProcessTokenProbe]::IsElevated($process) -or [ProcessTokenProbe]::IsAdministrator($process)) { throw 'Expected non-administrator' }
    Wait-For { $null -ne (Find-Id 'NavStorage') } 'navigation'
    (Find-Id 'NavStorage').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find-Id 'StoragePath') } 'storage page'
    if ($ShellOnly) {
        if ((Summary) -notmatch '尚未计算') { throw 'Unexpected automatic analysis' }
        if ($null -ne (Find-Id 'StorageTile0')) { throw 'Unexpected automatic treemap data' }
        $checks.Add('Release non-administrator storage page opens with no automatic scan or fabricated sizes')
        (Find-Id 'NavMemory').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Milliseconds 200
        (Find-Id 'NavStorage').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-For { $null -ne (Find-Id 'StorageSummary') } 'release navigation'
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Release window did not close cleanly' }
        $checks.Add('Release cached navigation and actual window exit code 0')
        @{ configuration=$Configuration; sourceScan=$false; isElevated=$false; isAdministrator=$false; checks=@($checks) } |
            ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'release-storage-shell.json') -Encoding UTF8
        $checks; return
    }
    Click 'StorageUp'
    Set-Path $fixture; Click 'ReadStorage'
    Wait-For { (Find-Id 'StorageStatus').Current.Name -match '尚无大小快照' } 'uninitialized state'
    Click 'AnalyzeStorage'
    Wait-For { (Summary) -match '100 逻辑字节.*4 文件' } '100-byte tree'
    Wait-For { $null -ne (Find-Id 'StorageTile0') } 'real treemap tiles'
    $checks.Add('Non-administrator real WinUI analyzes only isolated fixture: 100 bytes, 4 files')
    Click 'StorageTile0'; Wait-For { (Summary) -match '90 逻辑字节.*3 文件' } 'first level'
    Click 'StorageTile0'; Wait-For { (Summary) -match '70 逻辑字节.*2 文件' } 'second level'
    Click 'StorageTile0'; Wait-For { (Summary) -match '40 逻辑字节.*1 文件' } 'third level'
    Click 'StorageBack'; Wait-For { (Summary) -match '70 逻辑字节' } 'back'
    Click 'StorageForward'; Wait-For { (Summary) -match '40 逻辑字节' } 'forward'
    Click 'StorageUp'; Wait-For { (Summary) -match '70 逻辑字节' } 'up first'
    Click 'StorageUp'; Wait-For { (Summary) -match '90 逻辑字节' } 'up second'
    Click 'StorageUp'; Wait-For { (Summary) -match '100 逻辑字节' } 'root again'
    $checks.Add('Treemap drill-down through three levels, back, forward and up retain exact totals')
    [IO.File]::Move((Join-Path $fixture 'root.txt'),(Join-Path $deep 'moved.txt'))
    [IO.File]::WriteAllBytes((Join-Path $deep 'moved.txt'),[byte[]]::new(60))
    Click 'AnalyzeStorage'; Wait-For { (Summary) -match '150 逻辑字节.*4 文件' } 'rescan after move and resize'
    Set-Path (Join-Path $fixture 'empty'); Click 'ReadStorage'
    Wait-For { (Summary) -match '0 逻辑字节.*0 文件' } 'empty directory'
    if ($null -ne (Find-Id 'StorageTile0')) { throw 'Empty directory has stale treemap' }
    $checks.Add('Manual move/resize then rescan gives 150 bytes; empty directory has zero files and no stale tiles')
    Set-Path $fixture; Click 'ReadStorage'; Wait-For { (Summary) -match '150 逻辑字节' } 'root snapshot reload'
    (Find-Id 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find-Id 'SearchQuery') } 'shared search'
    (Find-Id 'SearchQuery').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('moved')
    Wait-For { (Find-Id 'SearchStatus').Current.Name -match '1–1 条' } 'same index search'
    $checks.Add('Search sees moved file in same active shared index generation')
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Application did not close cleanly' }
    $checks.Add('Actual window exits with code 0')
    @{ configuration='Debug'; isolatedStore=$true; isElevated=$false; isAdministrator=$false; checks=@($checks) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'storage-flow.json') -Encoding UTF8
    $checks; Write-Output (Join-Path $out 'storage-flow.json')
} finally {
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_RULE_DIRECTORY',$null,'Process')
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_INDEX_DIRECTORY',$null,'Process')
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
