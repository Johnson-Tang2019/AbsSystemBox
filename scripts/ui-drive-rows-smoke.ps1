# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/drive-rows-'+[guid]::NewGuid().ToString('N'))
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($env:SYSBOX_TEST_RULE_DIRECTORY)|Out-Null
@{FloatingBall=$false;KeepInTray=$false;HotKey=0;Theme=2}|ConvertTo-Json|Set-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json') -Encoding UTF8
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/DockWindowProbe.cs')
[DockWindowProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))|Out-Null
$process=[RestrictedUserLauncher]::Launch((Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'),'')
function Find($parent,[string]$id){$parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Find-All($parent,[string]$id){@($parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)))}
function Wait-For([scriptblock]$predicate,[string]$message){for($i=0;$i-lt150;$i++){if($process.HasExited){throw "Application exited: $message"};if(& $predicate){return};Start-Sleep -Milliseconds 100};throw "Timeout: $message"}
function Measure-Bar($bar,[string]$name){
    $rect=$bar.Current.BoundingRectangle
    $value=$bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    if($value-lt5){throw 'Fixture cannot measure a very short foreground segment'}
    $point=[DockWindowProbe+Point]::new();$point.X=[int]($rect.Left+$rect.Width*$value/200);$point.Y=[int]($rect.Top+$rect.Height/2)
    $owner=0u;[DockWindowProbe]::GetWindowThreadProcessId([DockWindowProbe]::WindowFromPoint($point),[ref]$owner)|Out-Null
    if($owner-ne$process.Id){throw 'Another window covers the progress-bar capture'}
    $bitmap=[System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height+8)
    try{
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try{$graphics.CopyFromScreen([int]$rect.Left,[int]$rect.Top-4,0,0,$bitmap.Size)}finally{$graphics.Dispose()}
        $bitmap.Save((Join-Path $out $name),[System.Drawing.Imaging.ImageFormat]::Png)
        $x=[int][math]::Floor($rect.Width*$value/200);$pink=0
        for($y=0;$y-lt$bitmap.Height;$y++){$color=$bitmap.GetPixel($x,$y);if($color.R-gt120-and$color.R-$color.G-gt25){$pink++}}
        return $pink
    }finally{$bitmap.Dispose()}
}
try{
    if([ProcessTokenProbe]::IsElevated($process)-or[ProcessTokenProbe]::IsAdministrator($process)){throw 'Expected ordinary-user fixture'}
    Wait-For {$process.Refresh();$process.MainWindowHandle-ne0} 'new overview'
    [DockWindowProbe]::ShowOnTop($process.MainWindowHandle)
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $roots=@([IO.DriveInfo]::GetDrives()|Select-Object -First 64|Where-Object{$_.DriveType-in@([IO.DriveType]::Fixed,[IO.DriveType]::Removable)}|ForEach-Object{$_.Name})
    Wait-For {(Find-All $main 'DashboardVolumeTitle').Count-eq$roots.Count} 'one row per reported local/removable drive'
    $titles=@(Find-All $main 'DashboardVolumeTitle'|ForEach-Object{$_.Current.Name})
    foreach($driveRoot in $roots){if(-not($titles|Where-Object{$_.StartsWith($driveRoot,[StringComparison]::OrdinalIgnoreCase)})){throw 'A drive was omitted from the overview'}}
    Wait-For {(Find $main 'DashboardMemoryUsageText').Current.Name-match'内存已用'} 'new memory bar'
    Start-Sleep -Milliseconds 300
    $dpi=[DockWindowProbe]::GetDpiForWindow($process.MainWindowHandle);$expected=[int][math]::Round(10*$dpi/96.0)
    $memoryThickness=Measure-Bar (Find $main 'DashboardMemoryUsage') 'memory-bar.png'
    if($memoryThickness-lt$expected-1){throw "Memory foreground is still thin: $memoryThickness"}
    $scrollers=$main.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,$true))
    foreach($scroller in $scrollers){$scroll=$scroller.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern);if($scroll.Current.VerticallyScrollable){$scroll.SetScrollPercent(-1,100)}}
    Start-Sleep -Milliseconds 300
    $bars=@(Find-All $main 'DashboardVolumeUsage'|Where-Object{-not$_.Current.IsOffscreen})
    if($bars.Count-lt1){throw 'No readable per-drive progress bars'}
    $diskThickness=Measure-Bar $bars[0] 'drive-bar.png'
    if($diskThickness-lt$expected-1){throw "Disk foreground is still thin: $diskThickness"}
    $details=@(Find-All $main 'DashboardVolumeDetail'|ForEach-Object{$_.Current.Name})
    $bounds=[DockWindowProbe]::Bounds($process.MainWindowHandle)
    $bitmap=[System.Drawing.Bitmap]::new($bounds.Width,$bounds.Height)
    try{$graphics=[System.Drawing.Graphics]::FromImage($bitmap);try{$graphics.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$bitmap.Size)}finally{$graphics.Dispose()};$bitmap.Save((Join-Path $out 'drives.png'),[System.Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
    $process.CloseMainWindow()|Out-Null
    if(-not$process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Fixture exit failed'}
    @{isElevated=$false;reportedDrives=$roots.Count;displayedRows=$titles.Count;readableBars=$bars.Count;unknownRows=@($details|Where-Object{$_-match'使用率未知'}).Count;memoryForegroundPixels=$memoryThickness;driveForegroundPixels=$diskThickness;dpi=$dpi;systemCleanupExecuted=$false;volumeScanned=$false;oldUiFlows='not rerun'}|ConvertTo-Json|Set-Content (Join-Path $out 'report.json') -Encoding UTF8
    Join-Path $out 'report.json'
}finally{
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not$process.HasExited){$process.CloseMainWindow()|Out-Null}
}
