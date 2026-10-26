# SPDX-License-Identifier: GPL-3.0-or-later
# Run with Windows PowerShell 5.1 in an interactive Windows session.
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $Demo,
    [switch] $StandardUser,
    [switch] $RestrictedUser,
    [ValidateRange(0, 100)][int] $StabilityCycles = 0,
    [ValidateRange(0, 180)][int] $MonitoringSeconds = 0
)
$ErrorActionPreference = 'Stop'
if ($StandardUser -and $RestrictedUser) { throw 'Choose either StandardUser or RestrictedUser' }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class SysBoxUiNative {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
 [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd,int x,int y,int w,int h,bool repaint);
 public static void Save(IntPtr hwnd, string path) {
  RECT r; GetWindowRect(hwnd, out r);
  using(var b = new Bitmap(r.Right-r.Left, r.Bottom-r.Top)) {
   using(var g = Graphics.FromImage(b)) {
    var dc=g.GetHdc();
    try { if(!PrintWindow(hwnd,dc,2)) throw new Exception("PrintWindow failed"); }
    finally { g.ReleaseHdc(dc); }
   }
   b.Save(path, ImageFormat.Png);
  }
 }
}
'@ -ReferencedAssemblies System.Drawing
[SysBoxUiNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "src/SysBox.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Build $Configuration first: $exe" }
$suffix = if ($Demo) { 'demo-argument' } else { 'normal' }
if ($StandardUser) { $suffix += '-standard-user' }
if ($RestrictedUser) { $suffix += '-restricted-user' }
$out = Join-Path $root "artifacts/ui/$Configuration-$suffix"
[IO.Directory]::CreateDirectory($out) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$expectedDemo = $Demo -and $Configuration -eq 'Debug'
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
$process = if ($RestrictedUser) {
    Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
    $arguments = if ($Demo) { '--demo' } else { '' }
    [RestrictedUserLauncher]::Launch($exe, $arguments)
} elseif ($StandardUser) {
    $arguments = if ($Demo) { '--demo' } else { '' }
    $existingIds = @([Diagnostics.Process]::GetProcessesByName('SysBox.App') | ForEach-Object { $_.Id })
    $shell = New-Object -ComObject Shell.Application
    $shellHandle = 0
    $desktop = $shell.Windows().FindWindowSW(0, 0, 8, [ref]$shellHandle, 1)
    if (-not $desktop) { throw 'An interactive desktop Shell is required for standard-user validation' }
    # Launch through the desktop, not through this test host's potentially elevated context.
    $desktop.Document.Application.ShellExecute($exe, $arguments, (Split-Path $exe), 'open', 1)
    $launched = $null
    for ($attempt=0; $attempt -lt 60 -and -not $launched; $attempt++) {
        Start-Sleep -Milliseconds 100
        $launched = [Diagnostics.Process]::GetProcessesByName('SysBox.App') | Where-Object {
            $_.Id -notin $existingIds -and $_.Path -eq $exe
        } | Select-Object -First 1
    }
    if (-not $launched) { throw 'The desktop Shell did not launch the expected app' }
    $launched
} elseif ($Demo) {
    Start-Process -FilePath $exe -ArgumentList '--demo' -WorkingDirectory (Split-Path $exe) -PassThru
} else {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
}
function Find-Id([string] $id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Find-Name([string] $name) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
}
function Wait-For([scriptblock] $condition, [string] $description) {
    for ($i=0; $i -lt 150; $i++) {
        $process.Refresh()
        if ($process.HasExited) { throw "App exited while waiting for $description" }
        if (& $condition) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $description"
}
function Select-Page([string] $id, [string] $title) {
    $item = Find-Id $id
    if (-not $item) { throw "Missing navigation item: $id" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $p = Find-Id 'PageTitle'; $p -and $p.Current.Name -eq $title } $id
}
function Invoke-Button([string] $id) {
    $button = Find-Id $id
    if (-not $button -or -not $button.Current.IsEnabled) { throw "Button unavailable: $id" }
    try { $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    catch { throw "Failed to invoke button $id : $($_.Exception.Message)" }
}
function Set-Theme([string] $name, [string] $expected) {
    $combo = Find-Id 'ThemePicker'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Wait-For { $null -ne (Find-Name $name) } "Theme option $name"
    (Find-Name $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { $label = Find-Id 'AppliedTheme'; $label -and $label.Current.Name -eq $expected } "Applied theme $name"
    Start-Sleep -Milliseconds 350
}
function Capture([string] $name) {
    # A covered DirectComposition window may not have a fresh surface for PrintWindow.
    [SysBoxUiNative]::ShowWindow($process.MainWindowHandle, 9) | Out-Null
    [SysBoxUiNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
    [SysBoxUiNative]::Save($process.MainWindowHandle, (Join-Path $out "$name.png"))
}
function Sample-Count {
    $label = Find-Id 'SampleCount'
    if ($label -and $label.Current.Name -match '(\d+) / 60') { return [int]$Matches[1] }
    return 0
}
function Scroll-Memory([double] $percent) {
    (Find-Id 'MemoryScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, $percent)
    Start-Sleep -Milliseconds 250
}
# ASCII source keeps this script compatible with Windows PowerShell's default encoding.
function U([string] $text) { [regex]::Unescape($text) }
$titles = @{
    NavDashboard = (U '\u6982\u89c8'); NavMemory = (U '\u5185\u5b58')
    NavSearch = (U '\u6587\u4ef6\u641c\u7d22'); NavStorage = (U '\u78c1\u76d8\u7a7a\u95f4')
    NavRules = (U '\u89c4\u5219'); NavDiagnostics = (U '\u8bca\u65ad'); NavSettings = (U '\u8bbe\u7f6e')
}
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 'main window'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $isElevated = [ProcessTokenProbe]::IsElevated($process)
    $isAdministrator = [ProcessTokenProbe]::IsAdministrator($process)
    if (($StandardUser -or $RestrictedUser) -and ($isElevated -or $isAdministrator)) { throw 'Non-administrator validation launched a privileged process' }
    Wait-For { $null -ne (Find-Id 'NavDashboard') } 'navigation'
    $dpi = [SysBoxUiNative]::GetDpiForWindow($process.MainWindowHandle)
    for ($cycle=0; $cycle -lt 3; $cycle++) {
        foreach ($id in @('NavMemory','NavSearch','NavStorage','NavRules','NavDiagnostics','NavSettings','NavDashboard')) {
            Select-Page $id $titles[$id]
        }
    }
    $checks.Add('All seven destinations selected through native UI Automation, three cycles')
    Select-Page 'NavDiagnostics' $titles.NavDiagnostics
    $xamlScale = (Find-Id 'UiScale').Current.Name
    Select-Page 'NavDashboard' $titles.NavDashboard
    $banner = Find-Id 'DemoBanner'
    if ($expectedDemo -ne ($null -ne $banner -and -not $banner.Current.IsOffscreen)) {
        throw 'Demo banner visibility does not match configuration'
    }
    $checks.Add("Demo mode expected: $expectedDemo")

    Select-Page 'NavSettings' $titles.NavSettings
    Set-Theme (U '\u6d45\u8272') (U '\u5f53\u524d\u663e\u793a\uff1a\u6d45\u8272')
    Select-Page 'NavDashboard' $titles.NavDashboard
    Capture 'dashboard-light'
    Select-Page 'NavSettings' $titles.NavSettings
    Set-Theme (U '\u6df1\u8272') (U '\u5f53\u524d\u663e\u793a\uff1a\u6df1\u8272')
    Capture 'settings-dark'
    Select-Page 'NavDashboard' $titles.NavDashboard
    Capture 'dashboard-dark'
    Select-Page 'NavSettings' $titles.NavSettings
    $personalize = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
    $systemExpected = if ($personalize.AppsUseLightTheme -eq 0) {
        U '\u5f53\u524d\u663e\u793a\uff1a\u6df1\u8272'
    } else { U '\u5f53\u524d\u663e\u793a\uff1a\u6d45\u8272' }
    Set-Theme (U '\u8ddf\u968f\u7cfb\u7edf') $systemExpected
    $checks.Add('Light, dark and system themes applied through the theme ComboBox')

    Select-Page 'NavMemory' $titles.NavMemory
    Wait-For { (Sample-Count) -ge 1 -and (Find-Id 'RefreshStatus').Current.IsEnabled } 'first memory sample'
    Wait-For { (Sample-Count) -ge 2 } 'automatic five-second sample'
    Invoke-Button 'ToggleMonitoring'
    Wait-For { (Find-Id 'MonitoringStatus').Current.Name -eq (U '\u81ea\u52a8\u5237\u65b0\u5df2\u505c\u6b62') } 'stop monitoring'
    $frozen = Sample-Count
    Start-Sleep -Milliseconds 5500
    if ((Sample-Count) -ne $frozen) { throw 'Samples continued after monitoring stopped' }
    $checks.Add('Five-second monitoring produces samples; stopping freezes history beyond a full interval')
    $memoryValues = @{
        total=(Find-Id 'TotalMemory').Current.Name; available=(Find-Id 'AvailableMemory').Current.Name
        usage=(Find-Id 'PhysicalUsage').Current.Name; commit=(Find-Id 'CommitMemory').Current.Name
        coverage=(Find-Id 'ProcessCoverage').Current.Name
    }
    if ($memoryValues.usage -notmatch '^\d+[.,]\d%$' -or $memoryValues.commit -notmatch 'GiB / .*GiB') {
        throw 'Physical usage or commit accounting is missing'
    }
    Scroll-Memory 0
    Capture 'memory-metrics'
    Scroll-Memory 100
    $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    Wait-For { (Find-Id 'ProcessList').FindAll([System.Windows.Automation.TreeScope]::Descendants, $rowCondition).Count -gt 0 } 'process rows'
    $firstRow = (Find-Id 'ProcessList').FindAll([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)[0]
    $cellCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $metricCells = @($firstRow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cellCondition) | Where-Object { $_.Current.Name -match 'MiB$' })
    if ($metricCells.Count -lt 2) { throw 'Working-set and private-commit cells are not populated' }
    Capture 'memory-processes'
    Scroll-Memory 0
    if ($expectedDemo) {
        Invoke-Button 'RefreshStatus'
        Wait-For { (Find-Id 'CancelOperation').Current.IsEnabled } 'loading state'
        Invoke-Button 'CancelOperation'
        Wait-For { -not (Find-Id 'CancelOperation').Current.IsEnabled } 'cancel complete'
        Capture 'cancelled'
        $checks.Add('Loading indicator and cancel button exercised on delayed demo client')
        Invoke-Button 'RefreshStatus'
        Select-Page 'NavSearch' $titles.NavSearch
        Start-Sleep -Milliseconds 2300
        Select-Page 'NavDashboard' $titles.NavDashboard
        Wait-For { -not (Find-Id 'CancelOperation').Current.IsEnabled } 'demo result'
        $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        if (-not ($texts | Where-Object { $_.Current.Name -match '6[.,]0 / 16[.,]0 GiB' })) { throw 'Demo result was not shown' }
        Capture 'demo-result'
        $checks.Add('Navigating away cancels pending memory request; dashboard loads explicit demo data')
    } else {
        Invoke-Button 'RefreshStatus'
        Wait-For { (Find-Id 'RefreshStatus').Current.IsEnabled } 'manual real sample'
        if ((Sample-Count) -ne ($frozen + 1)) { throw 'Manual refresh did not add exactly one sample' }
        if ($null -ne (Find-Id 'OperationError')) { throw 'Real memory read reported an error' }
        if ($memoryValues.coverage -notmatch '\d+ / \d+') { throw 'Process coverage counts are missing' }
        $checks.Add('Real physical/commit counters and process list displayed; manual refresh works while stopped')
        $memoryValues = @{
            total=(Find-Id 'TotalMemory').Current.Name; available=(Find-Id 'AvailableMemory').Current.Name
            usage=(Find-Id 'PhysicalUsage').Current.Name; commit=(Find-Id 'CommitMemory').Current.Name
            coverage=(Find-Id 'ProcessCoverage').Current.Name; sampledAt=(Find-Id 'LastSample').Current.Name
        }
        # Independent WMI performance-provider counters; no process paths are exported.
        $osCounter = Get-CimInstance Win32_OperatingSystem
        $memoryCounter = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
        $reference = @{
            utc=[DateTimeOffset]::UtcNow.ToString('O'); totalVisibleBytes=[uint64]$osCounter.TotalVisibleMemorySize*1024
            availableBytes=[uint64]$memoryCounter.AvailableBytes; committedBytes=[uint64]$memoryCounter.CommittedBytes
            commitLimitBytes=[uint64]$memoryCounter.CommitLimit
        }
        $freshTotal = (Find-Id 'TotalMemory').Current.Name
        if ($freshTotal -notmatch '(\d+[.,]\d+)') { throw 'Cannot parse displayed total memory' }
        $displayedGiB = [double]::Parse($Matches[1].Replace(',','.'), [Globalization.CultureInfo]::InvariantCulture)
        if ([Math]::Abs($displayedGiB - ($reference.totalVisibleBytes / 1GB)) -gt 0.06) { throw 'Physical total disagrees with OS visible memory' }
        $checks.Add('Displayed physical total agrees with independent Windows OS counter within rounding')
    }
    $resourceSamples = @()
    $continuousSamples = @()
    if ($MonitoringSeconds -gt 0) {
        Select-Page 'NavMemory' $titles.NavMemory
        Wait-For { (Sample-Count) -ge 1 } 'continuous monitor start'
        if ((Find-Id 'MonitoringStatus').Current.Name -eq (U '\u81ea\u52a8\u5237\u65b0\u5df2\u505c\u6b62')) { Invoke-Button 'ToggleMonitoring' }
        $continuousStartCount = Sample-Count
        for ($elapsed=0; $elapsed -lt $MonitoringSeconds; $elapsed += 5) {
            Start-Sleep -Seconds 5
            $process.Refresh()
            $continuousSamples += [ordered]@{seconds=$elapsed+5;historyCount=(Sample-Count);privateBytes=$process.PrivateMemorySize64;handles=$process.HandleCount}
        }
        if ((Sample-Count) -le $continuousStartCount) { throw 'Continuous monitoring did not add samples' }
        Select-Page 'NavSearch' $titles.NavSearch
        $checks.Add("Observed $MonitoringSeconds seconds of continuous sampling before leaving the page")
    }
    if ($StabilityCycles -gt 0) {
        for ($cycle=0; $cycle -lt $StabilityCycles; $cycle++) {
            Select-Page 'NavMemory' $titles.NavMemory
            Wait-For { (Sample-Count) -ge 1 -and (Find-Id 'RefreshStatus').Current.IsEnabled } 'stability memory sample'
            Select-Page 'NavSearch' $titles.NavSearch
            Start-Sleep -Milliseconds 250
            $process.Refresh()
            $resourceSamples += [ordered]@{cycle=$cycle+1;privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;handles=$process.HandleCount}
        }
        Start-Sleep -Milliseconds 5500
        $process.Refresh()
        $resourceSamples += [ordered]@{cycle='idle';privateBytes=$process.PrivateMemorySize64;workingSetBytes=$process.WorkingSet64;handles=$process.HandleCount}
        $checks.Add("Completed $StabilityCycles memory-page open/sample/leave cycles; resource observations recorded without forcing GC")
    }
    Select-Page 'NavSettings' $titles.NavSettings
    $rect = $window.Current.BoundingRectangle
    [SysBoxUiNative]::MoveWindow($process.MainWindowHandle, [int]$rect.X, [int]$rect.Y, 900, 740, $true) | Out-Null
    Capture 'compact-settings'
    if ((Find-Id 'PageTitle').Current.IsOffscreen) { throw 'Page heading disappeared at compact size' }
    $checks.Add('Compact 900 x 740 physical-pixel layout remains navigable')
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(10000)) { throw 'App did not exit after closing its window' }
    if ($process.ExitCode -ne 0) { throw "Nonzero app exit code: $($process.ExitCode)" }
    $checks.Add('Close window exits process with code 0')
    [ordered]@{
        configuration=$Configuration; demoArgument=[bool]$Demo; expectedDemo=$expectedDemo
        dpi=$dpi; scalePercent=($dpi/96*100); xamlScale=$xamlScale; isElevated=$isElevated; isAdministrator=$isAdministrator
        restrictedUser=[bool]$RestrictedUser; memory=$memoryValues; reference=$reference; resourceSamples=$resourceSamples; continuousSamples=$continuousSamples
        exitCode=$process.ExitCode; checks=@($checks)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'report.json') -Encoding UTF8
    $checks
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; $process.WaitForExit(10000) | Out-Null }
}
