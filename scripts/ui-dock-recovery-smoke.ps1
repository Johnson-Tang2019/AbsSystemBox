# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/ui/dock-recovery-'+[guid]::NewGuid().ToString('N'))
$env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'rules'
$env:SYSBOX_TEST_INDEX_DIRECTORY=Join-Path $out 'index'
$env:SYSBOX_TEST_PIPE_SUFFIX=[guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($env:SYSBOX_TEST_RULE_DIRECTORY)|Out-Null
@{FloatingBall=$true;KeepInTray=$false;HotKey=0;Theme=2;BallX=300;BallY=200}|ConvertTo-Json|Set-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'desktop.json') -Encoding UTF8
Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
Add-Type -Path (Join-Path $root 'tests/Windows/DockWindowProbe.cs')
[DockWindowProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))|Out-Null
$original=[DockWindowProbe+Point]::new();[DockWindowProbe]::GetCursorPos([ref]$original)|Out-Null
$exe=Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe'
$process=[RestrictedUserLauncher]::Launch($exe,'')
function Find($parent,[string]$id){$parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Wait-For([scriptblock]$predicate,[string]$message){for($i=0;$i-lt100;$i++){if($process.HasExited){throw "Application exited: $message"};if(& $predicate){return};Start-Sleep -Milliseconds 50};throw "Timeout: $message"}
function Names($parent){@($parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name})}
function Capture([IntPtr]$window,[string]$name){
    $rect=[DockWindowProbe]::Bounds($window)
    $bitmap=[System.Drawing.Bitmap]::new($rect.Width,$rect.Height)
    try{
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try{$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)}finally{$graphics.Dispose()}
        $bitmap.Save((Join-Path $out $name),[System.Drawing.Imaging.ImageFormat]::Png)
        $black=0;$count=0
        for($y=[int]($rect.Height/3);$y-lt[math]::Floor($rect.Height*2/3);$y++){for($x=[int]($rect.Width/3);$x-lt[math]::Floor($rect.Width*2/3);$x++){$color=$bitmap.GetPixel($x,$y);if($color.R-lt10-and$color.G-lt10-and$color.B-lt10){$black++};$count++}}
        return $black/[double]$count
    }finally{$bitmap.Dispose()}
}
function Drag-To([int]$x,[int]$y){
    $rect=[DockWindowProbe]::Bounds($handle)
    [DockWindowProbe]::Move(($rect.Left+$rect.Right)/2,($rect.Top+$rect.Bottom)/2);Start-Sleep -Milliseconds 80
    $point=[DockWindowProbe+Point]::new();[DockWindowProbe]::GetCursorPos([ref]$point)|Out-Null
    $owner=0u;[DockWindowProbe]::GetWindowThreadProcessId([DockWindowProbe]::WindowFromPoint($point),[ref]$owner)|Out-Null
    @{phase='drag-start';cursor=$point;owner=$owner;expectedProcess=$process.Id;bounds=$rect}|ConvertTo-Json -Compress -Depth 3|Add-Content (Join-Path $out 'input.jsonl') -Encoding UTF8
    if($owner-ne$process.Id){throw 'Another window is covering the fixture ball'}
    [DockWindowProbe]::Button(2)
    Start-Sleep -Milliseconds 100
    [DockWindowProbe]::Move(($rect.Left+$rect.Right)/2+10,($rect.Top+$rect.Bottom)/2+10);Start-Sleep -Milliseconds 60
    [DockWindowProbe]::Move($x,$y);Start-Sleep -Milliseconds 80
    [DockWindowProbe]::Button(4)
}
try{
    if([ProcessTokenProbe]::IsElevated($process)-or[ProcessTokenProbe]::IsAdministrator($process)){throw 'Expected ordinary-user fixture'}
    Wait-For {$process.Refresh();$process.MainWindowHandle-ne0} 'main window'
    $main=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $windowPattern=$main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    Wait-For {(Find $main 'DashboardMemoryUsageText').Current.Name-match'内存已用'} 'new memory gauge'
    Wait-For {(Find $main 'DashboardStorageUsageText').Current.Name-match'磁盘已用'} 'new disk gauge'
    $memory=(Find $main 'DashboardMemoryUsage').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    $disk=(Find $main 'DashboardStorageUsage').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    if($memory-lt0-or$memory-gt100-or$disk-lt0-or$disk-gt100){throw 'Gauge outside capacity range'}
    $headings=@('DashboardMemoryTitle','DashboardSearchTitle','DashboardStorageTitle')|ForEach-Object{(Find $main $_).Current.BoundingRectangle}
    if([math]::Abs($headings[0].Left-$headings[1].Left)-gt2-or[math]::Abs($headings[0].Left-$headings[2].Left)-gt2-or$headings[0].Top-ge$headings[1].Top-or$headings[1].Top-ge$headings[2].Top){throw 'Overview cards are not vertically stacked'}
    Capture $process.MainWindowHandle 'overview.png'|Out-Null
    $ball=$null
    Wait-For {$script:ball=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))|Where-Object{$_.Current.Name-eq'AbsSystemBox 悬浮球'}|Select-Object -First 1;$null-ne$ball} 'floating ball'
    $handle=[IntPtr]$ball.Current.NativeWindowHandle
    $area=[DockWindowProbe]::WorkArea($handle);$dpi=[DockWindowProbe]::GetDpiForWindow($handle);$size=[int][math]::Ceiling(64*$dpi/96)
    $gdiBefore=[DockWindowProbe]::GetGuiResources($process.Handle,0)
    $cycles=0;$captures=@()
    foreach($edge in @('left','right','top','bottom')){
        $targetX=($area.Left+$area.Right)/2;$targetY=($area.Top+$area.Bottom)/2
        if($edge-eq'left'){$targetX=$area.Left+$size/2};if($edge-eq'right'){$targetX=$area.Right-$size/2}
        if($edge-eq'top'){$targetY=$area.Top+$size/2};if($edge-eq'bottom'){$targetY=$area.Bottom-$size/2}
        Drag-To $targetX $targetY
        for($cycle=0;$cycle-lt8;$cycle++){
            [DockWindowProbe]::Move(($area.Left+$area.Right)/2+150,($area.Top+$area.Bottom)/2+150)
            Wait-For {$r=[DockWindowProbe]::Region($handle);[math]::Min($r.Width,$r.Height)-lt$size/2} 'collapsed region'
            $bounds=[DockWindowProbe]::Bounds($handle);$region=[DockWindowProbe]::Region($handle)
            if($bounds.Width-ne$size-or$bounds.Height-ne$size){throw 'Compositor window resized while collapsed'}
            [DockWindowProbe]::Move(($bounds.Left+$region.Left+$region.Width/2),($bounds.Top+$region.Top+$region.Height/2))
            Start-Sleep -Milliseconds 100
            Wait-For {$r=[DockWindowProbe]::Region($handle);$r.Width-ge$size-and$r.Height-ge$size} 'expanded region'
            $bounds=[DockWindowProbe]::Bounds($handle)
            if($bounds.Width-ne$size-or$bounds.Height-ne$size){throw 'Compositor window resized after expansion'}
            if($cycle-eq7){
                Start-Sleep -Milliseconds 100
                $black=Capture $handle ($edge+'-restored.png')
                if($black-gt0.05){throw "Black restored ball: $edge"}
                $captures+=@{edge=$edge;blackFraction=$black}
            }
            if((Names $ball)-contains'内存'){throw 'Removed inner label remains'}
            $cycles++
        }
    }
    Drag-To ($area.Left+500) ($area.Top+300)
    Wait-For {(Get-Content (Join-Path $env:SYSBOX_TEST_RULE_DIRECTORY 'floating-layout.json') -Raw|ConvertFrom-Json).edge-eq'None'} 'detach after stress'
    $gdiAfter=[DockWindowProbe]::GetGuiResources($process.Handle,0)
    if($gdiAfter-gt$gdiBefore+12){throw "Unexpected GDI resource growth: $gdiBefore -> $gdiAfter"}
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    $bounds=[DockWindowProbe]::Bounds($handle);[DockWindowProbe]::Move(($bounds.Left+$bounds.Right)/2,($bounds.Top+$bounds.Bottom)/2)
    [DockWindowProbe]::Button(2);[DockWindowProbe]::Button(4)
    Wait-For {$windowPattern.Current.WindowVisualState-ne[System.Windows.Automation.WindowVisualState]::Minimized} 'upstream single click opens main'
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    [DockWindowProbe]::Button(8);[DockWindowProbe]::Button(16)
    Wait-For {$windowPattern.Current.WindowVisualState-ne[System.Windows.Automation.WindowVisualState]::Minimized} 'upstream right click opens main'
    $process.CloseMainWindow()|Out-Null
    if(-not$process.WaitForExit(15000)-or$process.ExitCode-ne0){throw 'Fixture exit failed'}
    @{isElevated=$false;cycles=$cycles;edges=4;dpi=$dpi;stableWindowPixels=$size;restoredCaptures=$captures;gdiBefore=$gdiBefore;gdiAfter=$gdiAfter;singleClickOpensMain=$true;rightClickOpensMain=$true;verticalOverview=$true;memoryUsage=$memory;diskUsage=$disk;systemCleanupExecuted=$false;volumeScanned=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'report.json') -Encoding UTF8
    Join-Path $out 'report.json'
}catch{
    if($handle){
        $point=[DockWindowProbe+Point]::new();[DockWindowProbe]::GetCursorPos([ref]$point)|Out-Null
        $at=[DockWindowProbe]::WindowFromPoint($point);$owner=0u;[DockWindowProbe]::GetWindowThreadProcessId($at,[ref]$owner)|Out-Null
        @{cursor=$point;windowAtCursor=$at.ToInt64();owner=$owner;expected=$handle.ToInt64();bounds=[DockWindowProbe]::Bounds($handle);region=[DockWindowProbe]::Region($handle)}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'failure-native.json') -Encoding UTF8
        Capture $handle 'failure-ball.png'|Out-Null
    }
    throw
}finally{
    [DockWindowProbe]::Button(4)
    [DockWindowProbe]::Move($original.X,$original.Y)
    foreach($name in @('SYSBOX_TEST_RULE_DIRECTORY','SYSBOX_TEST_INDEX_DIRECTORY','SYSBOX_TEST_PIPE_SUFFIX')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    if(-not$process.HasExited){$process.CloseMainWindow()|Out-Null}
}
