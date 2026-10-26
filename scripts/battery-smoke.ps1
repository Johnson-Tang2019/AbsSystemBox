# SPDX-License-Identifier: GPL-3.0-or-later
param([ValidateSet('Debug','Release')][string] $Configuration='Debug',[switch] $Demo,[switch] $RestrictedUser)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root 'artifacts/ui'
[IO.Directory]::CreateDirectory($out) | Out-Null
if($Configuration -eq 'Debug') { $env:SYSBOX_TEST_RULE_DIRECTORY=Join-Path $out 'battery-rule-store' }
$exe=Join-Path $root "src/SysBox.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe"
Add-Type -Path (Join-Path $root 'tests/Windows/ProcessTokenProbe.cs')
if($RestrictedUser) {
    Add-Type -Path (Join-Path $root 'tests/Windows/RestrictedUserLauncher.cs')
    $process=[RestrictedUserLauncher]::Launch($exe,'--demo')
} else {
    $arguments=if($Demo){'--demo'}else{''}
    $process=if($arguments){Start-Process -FilePath $exe -ArgumentList $arguments -PassThru}else{Start-Process -FilePath $exe -PassThru}
}
function Find-Id([string] $id) {
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
}
function Wait-For([scriptblock] $condition,[string] $description) {
    for($i=0;$i -lt 150;$i++) {
        $process.Refresh();if($process.HasExited){throw "App exited: $description"}
        if(& $condition){return};Start-Sleep -Milliseconds 100
    };throw "Timeout: $description"
}
function Select-Page([string] $id) {
    (Find-Id $id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}
$checks=[Collections.Generic.List[string]]::new()
try {
    Wait-For {$process.Refresh();$process.MainWindowHandle -ne 0} 'window'
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For {$null -ne (Find-Id 'NavDiagnostics')} 'navigation'
    $elevated=[ProcessTokenProbe]::IsElevated($process)
    $administrator=[ProcessTokenProbe]::IsAdministrator($process)
    if($RestrictedUser -and ($elevated -or $administrator)){throw 'Expected restricted non-admin process'}
    Select-Page 'NavDiagnostics'
    Wait-For {(Find-Id 'BatterySourceStatus').Current.Name -match 'Charging|NotPresent|Idle|Discharging|Unavailable'} 'battery report'
    $status=(Find-Id 'BatterySourceStatus').Current.Name
    $expectedDemo=$Demo -and $Configuration -eq 'Debug'
    if($status -match 'Unavailable'){throw 'Real battery API unavailable on this host'}
    $charging=$status -match 'Charging'
    if($expectedDemo -and -not $charging){throw 'Demo battery not charging'}
    foreach($page in @('NavDashboard','NavMemory','NavDashboard')) {
        Select-Page $page
        if($charging) {
            Wait-For {$null -ne (Find-Id 'BatteryChargePower')} 'charging card'
            if($expectedDemo -and ((Find-Id 'BatteryChargePower').Current.Name -notmatch '45.0' -or (Find-Id 'BatteryTimeToFull').Current.Name -notmatch '60')){throw 'Incorrect demo charging display'}
        } else {
            Start-Sleep -Milliseconds 600
            $card=Find-Id 'ChargingBatteryCard'
            if($card -and -not $card.Current.IsOffscreen){throw 'Charging card shown when not charging'}
        }
    }
    $checks.Add('Battery card conditional visibility checked on dashboard and memory, including navigation')
    if($expectedDemo){$checks.Add('Explicit Debug demo shows 45.0 W net charge and estimated 60 minutes')}
    if(-not $expectedDemo){$checks.Add('Actual Windows BatteryReport read, no demo hardware values')}
    if($Configuration -eq 'Release') {
        Select-Page 'NavRules'
        Wait-For {(Find-Id 'RuleObservation').Current.Name -match 'v\d+'} 'Release real rule observer'
        if((Find-Id 'ActiveRule').Current.Name -notmatch 'v1'){throw 'Expected initial default Release rule version'}
        $checks.Add('Release rule page loads default v1 and real notification-only observer')
    }
    $process.CloseMainWindow() | Out-Null
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0){throw 'App did not exit cleanly'}
    $checks.Add('Actual WinUI process exited with code 0')
    $name=if($expectedDemo){'battery-demo'}else{"battery-$Configuration-real"}
    @{configuration=$Configuration;demo=$expectedDemo;isElevated=$elevated;isAdministrator=$administrator;batteryStatus=$status;physicalBatteryCount=@(Get-CimInstance Win32_Battery).Count;checks=@($checks)} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "$name.json") -Encoding UTF8
    $checks
} finally {
    [Environment]::SetEnvironmentVariable('SYSBOX_TEST_RULE_DIRECTORY',$null,'Process')
    if(-not $process.HasExited){$process.CloseMainWindow() | Out-Null}
}
