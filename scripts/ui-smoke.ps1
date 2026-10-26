# SPDX-License-Identifier: GPL-3.0-or-later
# Run with Windows PowerShell 5.1 in an interactive Windows session.
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $Demo
)
$ErrorActionPreference = 'Stop'
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
$out = Join-Path $root "artifacts/ui/$Configuration-$suffix"
[IO.Directory]::CreateDirectory($out) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$expectedDemo = $Demo -and $Configuration -eq 'Debug'
$process = if ($Demo) {
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
    for ($i=0; $i -lt 60; $i++) {
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
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
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
    Start-Sleep -Milliseconds 200
    [SysBoxUiNative]::Save($process.MainWindowHandle, (Join-Path $out "$name.png"))
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
    Invoke-Button 'RefreshStatus'
    if ($expectedDemo) {
        Wait-For { (Find-Id 'CancelOperation').Current.IsEnabled } 'loading state'
        Capture 'loading'
        Invoke-Button 'CancelOperation'
        Wait-For { -not (Find-Id 'CancelOperation').Current.IsEnabled } 'cancel complete'
        Capture 'cancelled'
        $checks.Add('Loading indicator and cancel button exercised on delayed demo client')
        Invoke-Button 'RefreshStatus'
        Select-Page 'NavSearch' $titles.NavSearch
        Start-Sleep -Milliseconds 2300
        Select-Page 'NavDashboard' $titles.NavDashboard
        $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        if ($texts | Where-Object { $_.Current.Name -match '6[.,]0 / 16[.,]0 GiB' }) { throw 'Late demo value leaked across pages' }
        Invoke-Button 'RefreshStatus'
        Wait-For { -not (Find-Id 'CancelOperation').Current.IsEnabled } 'demo result'
        $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        if (-not ($texts | Where-Object { $_.Current.Name -match '6[.,]0 / 16[.,]0 GiB' })) { throw 'Demo result was not shown' }
        Capture 'demo-result'
        $checks.Add('Navigating away cancels pending request; fresh page can load demo result')
    } else {
        Wait-For { $null -ne (Find-Id 'OperationError') } 'structured unavailable state'
        Capture 'unavailable'
        $checks.Add('Unavailable service displayed as an explicit information state without fake metrics')
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
        dpi=$dpi; scalePercent=($dpi/96*100); xamlScale=$xamlScale; exitCode=$process.ExitCode; checks=@($checks)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'report.json') -Encoding UTF8
    $checks
}
finally {
    $process.Refresh()
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; $process.WaitForExit(10000) | Out-Null }
}
