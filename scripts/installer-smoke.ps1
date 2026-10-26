# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory=$true)][string] $OldInstaller,[Parameter(Mandatory=$true)][string] $NewInstaller,[string] $ExpectedVersion='0.1.0-preview.1',[switch] $RestrictedUser,[string] $TestFolder='installer-validation')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($TestFolder -notmatch '^installer-validation[-a-zA-Z0-9]*$'){throw 'Invalid test folder'}
$target=Join-Path $root "artifacts/$TestFolder/AbsSystemBox"
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AbsSystemBox.JohnsonTang2019'
if(Test-Path -LiteralPath $registry){throw 'An installation already exists; refusing to replace it during a test'}
if(Test-Path -LiteralPath $target){throw 'Use a fresh installer-validation directory'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if($RestrictedUser){
    Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
    Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
}
$checks=[Collections.Generic.List[string]]::new()
$rulePath=Join-Path $env:LOCALAPPDATA 'SysBox/rule-state.json'
$ruleBefore=if(Test-Path -LiteralPath $rulePath){[IO.File]::ReadAllText($rulePath)}else{$null}
function Start-TestProcess([string] $path,[string[]] $arguments=@(),[switch] $Visible) {
    if($RestrictedUser){
        $p=[RestrictedUserLauncher]::Launch([IO.Path]::GetFullPath($path),($arguments -join ' '))
        if([ProcessTokenProbe]::IsElevated($p) -or [ProcessTokenProbe]::IsAdministrator($p)){throw 'Expected a restricted non-admin test process'}
        return $p
    }
    if($arguments.Count){return Start-Process -FilePath $path -ArgumentList $arguments -WindowStyle Hidden -PassThru}
    return Start-Process -FilePath $path -PassThru
}
function Install([string] $path,[int] $expected=0) {
    $p=Start-TestProcess $path @('/S','/ACCEPTLICENSE',"/D=$target")
    if(-not $p.WaitForExit(180000)){throw 'Installer timed out'}
    if($p.ExitCode -ne $expected){throw "Installer exit $($p.ExitCode), expected $expected"}
}
function Open-App([string] $version) {
    $script:app=Start-TestProcess (Join-Path $target 'app/SysBox.App.exe')
    for($i=0;$i -lt 150;$i++){
        $app.Refresh();if($app.HasExited){throw 'Installed app exited early'}
        if($app.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 100
    }
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    $nav=$null
    for($i=0;$i -lt 100 -and -not $nav;$i++){
        $nav=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'NavDiagnostics'))
        if(-not $nav){Start-Sleep -Milliseconds 100}
    }
    if(-not $nav){throw 'Installed WinUI navigation unavailable'}
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 600
    $all=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
    if(-not @($all | Where-Object {$_.Current.Name -like "*$version*"}).Count){throw 'Installed diagnostic version incorrect'}
    foreach($pair in @(@('NavRules','RuleObservation','v\d+'),@('NavMemory','PhysicalUsage','\d'))){
        $item=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$pair[0]))
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $ready=$false
        for($i=0;$i -lt 100 -and -not $ready;$i++){
            $label=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$pair[1]))
            $ready=$label -and $label.Current.Name -match $pair[2]
            if(-not $ready){Start-Sleep -Milliseconds 100}
        }
        if(-not $ready){throw "Installed page failed to load real data: $($pair[0])"}
    }
}
function Close-App {
    $app.CloseMainWindow() | Out-Null
    if(-not $app.WaitForExit(15000) -or $app.ExitCode -ne 0){throw 'Installed app did not exit cleanly'}
}
Install $OldInstaller
if((Get-ItemProperty -LiteralPath $registry).DisplayVersion -ne '0.1.0-preview.0'){throw 'Initial registration incorrect'}
$checks.Add('Fresh per-user installation registered one stable product identity')
$unknown=Join-Path $target 'keep-user-note.txt';[IO.File]::WriteAllText($unknown,'preserve unknown file')
$obsolete=Join-Path $target 'app/obsolete-owned-test.txt';[IO.File]::WriteAllText($obsolete,'old version owned file')
[IO.File]::AppendAllText((Join-Path $target 'app/installed-files.txt'),"obsolete-owned-test.txt`r`n",[Text.UTF8Encoding]::new($false))
Open-App '0.1.0-preview.0'
Install $NewInstaller 4
$second=Start-TestProcess (Join-Path $target 'app/SysBox.App.exe')
if(-not $second.WaitForExit(15000) -or $second.ExitCode -ne 0 -or $app.HasExited){throw 'Single-instance protection failed'}
Close-App
$checks.Add('Installed old WinUI app runs; update refuses a running app; duplicate instance exits safely')
Install $NewInstaller
if((Get-ItemProperty -LiteralPath $registry).DisplayVersion -ne $ExpectedVersion){throw 'Upgrade registration incorrect'}
if(Test-Path -LiteralPath (Join-Path $target 'app/obsolete-owned-test.txt')){throw 'Obsolete file leaked into new payload'}
if(Test-Path -LiteralPath (Join-Path $target 'app.previous')){throw 'Owned old payload was not cleaned'}
if([IO.File]::ReadAllText($unknown) -ne 'preserve unknown file'){throw 'Unknown file changed'}
$checks.Add('Direct overwrite upgrade uses same directory and identity, switches complete payload and removes obsolete owned files')
Open-App $ExpectedVersion
Close-App
Install $OldInstaller 3
Install $NewInstaller
$checks.Add('Downgrade blocked; same-version repair succeeds')
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'AbsSystemBox/AbsSystemBox.lnk'))
if($shortcut.TargetPath -ne (Join-Path $target 'app/SysBox.App.exe')){throw 'Shortcut points to wrong payload'}
if($ruleBefore -and [IO.File]::ReadAllText($rulePath) -ne $ruleBefore){throw 'User rule store changed during upgrade test'}
$checks.Add('Stable start-menu shortcut and existing user rules preserved across upgrade/repair')
$userInApp=Join-Path $target 'app/keep-unknown.txt';[IO.File]::WriteAllText($userInApp,'preserve on uninstall')
$uninstaller=Join-Path $target 'Uninstall.exe'
$p=Start-TestProcess $uninstaller @('/S')
if(-not $p.WaitForExit(120000) -or $p.ExitCode -ne 0){throw 'Uninstall failed'}
for($i=0;$i -lt 200 -and ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $uninstaller));$i++){Start-Sleep -Milliseconds 100}
if(Test-Path -LiteralPath $registry){throw 'Uninstall registration still exists'}
if(Test-Path -LiteralPath (Join-Path $target 'app/SysBox.App.exe')){throw 'App executable not removed'}
if([IO.File]::ReadAllText($unknown) -ne 'preserve unknown file' -or [IO.File]::ReadAllText($userInApp) -ne 'preserve on uninstall'){throw 'Unknown files removed'}
if($ruleBefore -and [IO.File]::ReadAllText($rulePath) -ne $ruleBefore){throw 'User rules removed during uninstall'}
$checks.Add('Uninstall removes owned executable/registration and preserves user rules plus unknown files')
$checks.Add('Both installed versions started real WinUI windows, displayed expected version and exited with code 0')
$checks.Add('Installed rules observer and real read-only memory metrics loaded in both versions')
@{initialVersion='0.1.0-preview.0';upgradedVersion=$ExpectedVersion;restrictedNonAdministrator=[bool]$RestrictedUser;checks=@($checks);existingUserRulesPreserved=($null -ne $ruleBefore);unknownFilesPreserved=$true} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root "artifacts/$TestFolder/report.json") -Encoding UTF8
$checks
