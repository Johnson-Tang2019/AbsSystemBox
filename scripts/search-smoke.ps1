# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root ('artifacts/ui/m5-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $out 'sample'
[IO.Directory]::CreateDirectory($fixture) | Out-Null
for ($i=0; $i -lt 10050; $i++) { [IO.File]::WriteAllText((Join-Path $fixture ('model-{0:D5}.pt' -f $i)), 'fixture') }
[IO.File]::WriteAllText((Join-Path $fixture '中文 模型.txt'), 'fixture')
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
    for ($i=0; $i -lt 300; $i++) {
        $process.Refresh(); if ($process.HasExited) { throw "Application exited: $description" }
        if (& $condition) { return }; Start-Sleep -Milliseconds 100
    }; throw "Timeout: $description"
}
function Click([string] $id) { (Find-Id $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Query([string] $text) { (Find-Id 'SearchQuery').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Status { (Find-Id 'SearchStatus').Current.Name }
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'window'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ([ProcessTokenProbe]::IsElevated($process) -or [ProcessTokenProbe]::IsAdministrator($process)) { throw 'Expected non-administrator' }
    Wait-For { $null -ne (Find-Id 'NavSearch') } 'navigation'
    (Find-Id 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $null -ne (Find-Id 'SearchQuery') } 'search page'
    Query 'model'; Wait-For { (Status) -match '尚未建立索引' } 'uninitialized state'
    (Find-Id 'IndexPath').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixture)
    Click 'StartIndex'; Wait-For { (Find-Id 'IndexStatus').Current.Name -match '扫描完成.*10,051 文件' } 'fixture index'
    Wait-For { (Status) -match '1–100.*还有更多' } 'first page'
    $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $realized = (Find-Id 'SearchResults').FindAll([System.Windows.Automation.TreeScope]::Children,$rowCondition).Count
    if ($realized -gt 100) { throw 'Unbounded result controls' }
    Click 'NextSearchPage'; Wait-For { (Status) -match '101–200' } 'next page'
    Click 'PreviousSearchPage'; Wait-For { (Status) -match '1–100' } 'previous page'
    $checks.Add('10,051 real fixture files indexed; search pages remain at 100 records and native ListView is bounded')
    Query 'unknown:value'; Wait-For { (Status) -match '不支持的筛选字段' } 'invalid filter'
    Query 'nothing-matches-this'; Wait-For { (Status) -match '没有匹配结果' } 'empty state'
    Query 'm'; Query 'model'; Query 'ext:txt'; Query '"中文 模型"'
    Wait-For { (Status) -match '1–1 条' } 'latest debounced query'
    Start-Sleep -Milliseconds 500
    Wait-For {
        try {
            $items = (Find-Id 'SearchResults').FindAll([System.Windows.Automation.TreeScope]::Children,$rowCondition)
            $item = $items | Where-Object { $_.Current.Name -match '中文' } | Select-Object -First 1
            if ($null -eq $item) { return $false }
            $virtualized = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern,[ref]$virtualized)) { $virtualized.Realize() }
            $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return $true
        } catch { return $false }
    } 'latest native result row selected'
    Click 'OpenSearchResult'; Wait-For { $null -ne (Find-Id 'CloseButton') } 'open confirmation'
    Click 'CloseButton'
    $checks.Add('Invalid, empty, rapid input and Chinese phrase queries verified; opening remains behind user confirmation')
    (Find-Id 'NavMemory').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 200
    (Find-Id 'NavSearch').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { (Status) -match '1–1 条' } 'query restored after navigation'
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Application did not close cleanly' }
    $checks.Add('Navigation and actual WinUI exit code 0')
    @{ configuration='Debug'; isolatedStore=$true; fixtureFiles=10051; realizedResultControls=$realized; isElevated=$false; isAdministrator=$false; checks=@($checks) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'search-flow.json') -Encoding UTF8
    $checks; Write-Output (Join-Path $out 'search-flow.json')
} finally {
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_RULE_DIRECTORY',$null,'Process')
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_INDEX_DIRECTORY',$null,'Process')
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null }
}
