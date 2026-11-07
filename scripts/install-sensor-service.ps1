# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory=$true)][ValidatePattern('^S-1-5-21-[0-9-]+$')][string]$UserSid,[switch]$ReviewOnly)
$ErrorActionPreference='Stop'
if(-not[Environment]::Is64BitProcess){throw '请使用 64 位 Windows PowerShell 管理传感器服务。'}
$product='AbsSystemBox.JohnsonTang2019.Sensors'
$target=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AbsSystemBox/SensorService'
$policyRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AbsSystemBox'
$policyDirectory=Join-Path $policyRoot 'SensorService'
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'sensors'
$client=Join-Path (Split-Path $PSScriptRoot -Parent) 'SysBox.App.exe'
$admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$sid=[Security.Principal.SecurityIdentifier]::new($UserSid)
if($ReviewOnly){[pscustomobject]@{service='AbsSystemBox.Sensors';account='LocalSystem';target=$target;userSid=$sid.Value;client=$client;operations='仅固定温度/RPM 读取；无任意命令、风扇控制或文件扫描。'};return}
if(-not([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '请通过独立设置程序取得管理员授权。'}
$installed=Get-ItemProperty -LiteralPath ('Registry::HKEY_USERS\'+$UserSid+'\Software\Microsoft\Windows\CurrentVersion\Uninstall\AbsSystemBox.JohnsonTang2019') -ErrorAction Stop
if(-not$installed.InstallLocation-or-not[string]::Equals([IO.Path]::GetFullPath($client),[IO.Path]::GetFullPath((Join-Path $installed.InstallLocation 'app/SysBox.App.exe')),[StringComparison]::OrdinalIgnoreCase)){throw '只有已安装的 SystemBox 界面可以获得持久传感器授权。'}
$setupLock=[Threading.Mutex]::new($false,'Global\AbsSystemBox.Sensors.Setup')
if(-not$setupLock.WaitOne(0)){$setupLock.Dispose();throw '另一个传感器服务维护任务正在运行。'}
try {
function Check-NoReparse([string]$Path){if(Test-Path -LiteralPath $Path){if((Get-Item -LiteralPath $Path -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw '安装路径经过重解析点，已拒绝修改。'}};for($item=[IO.DirectoryInfo]::new((Split-Path $Path -Parent));$item;$item=$item.Parent){if($item.Exists -and ($item.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw '安装路径经过重解析点，已拒绝修改。'}}}
function Check-Trusted([string]$Path){
    Check-NoReparse $Path
    if(-not(Test-Path -LiteralPath $Path)){return}
    $acl=Get-Acl -LiteralPath $Path
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value-notin$trusted){throw '现有目录或文件的所有者不可信，已保留原内容。'}
    $write=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.PropagationFlags-band[Security.AccessControl.PropagationFlags]::InheritOnly){continue}
        if($rule.AccessControlType-eq'Allow'-and$rule.IdentityReference.Value-notin$trusted-and($rule.FileSystemRights-band$write)){throw '现有目录或文件允许不可信主体写入，已保留原内容。'}
    }
}
function Protect([string]$Path){
    Check-NoReparse $Path
    if(Test-Path -LiteralPath $Path){$owner=(Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]);if($owner.Value-notin@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')){throw '现有服务目录的所有者不可信，已保留原内容。'}}else{[IO.Directory]::CreateDirectory($Path)|Out-Null}
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin)
    foreach($principal in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'));Set-Acl -LiteralPath $Path -AclObject $acl
}
function Seal-File([string]$Path){
    Check-NoReparse $Path
    $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($admin)
    foreach($principal in @($admin,$system)){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,'FullControl','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','Allow'));Set-Acl -LiteralPath $Path -AclObject $acl
}
if(-not(Test-Path -LiteralPath (Join-Path $source 'sensors-files.json'))-or-not(Test-Path -LiteralPath $client)){throw '请使用完整安装版本中的设置脚本。'}
if((Get-Item -LiteralPath (Join-Path $source 'sensors-files.json')).Length-gt2097152){throw '传感器服务清单过大。'}
$manifest=Get-Content (Join-Path $source 'sensors-files.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.product-ne$product-or$manifest.files.Count-lt1-or$manifest.files.Count-gt2000){throw '传感器服务清单无效。'}
$paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$totalBytes=0L
foreach($file in $manifest.files){if($file.path-isnot[string]-or$file.path.Length-lt1-or$file.path.Length-gt800-or[IO.Path]::IsPathRooted($file.path)-or(($file.path-split'[/\\]')|Where-Object{$_-in@('..','.','')-or$_-ne$_.TrimEnd(' ','.')-or$_.IndexOfAny([IO.Path]::GetInvalidFileNameChars())-ge0-or($_-split'\.')[0]-match'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$'})-or$file.path.Contains(':')-or$file.path.Contains("`r")-or$file.path.Contains("`n")-or$file.sha256-notmatch'^[A-Fa-f0-9]{64}$'-or-not$paths.Add($file.path.Replace('/','\'))){throw '传感器负载路径或摘要无效。'};$candidate=[IO.Path]::GetFullPath((Join-Path $source $file.path));if(-not$candidate.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '传感器负载路径超出源目录。'};Check-NoReparse $candidate;$length=(Get-Item -LiteralPath $candidate).Length;$totalBytes+=$length;if($length-gt67108864-or$totalBytes-gt536870912){throw '传感器负载超出大小上限。'};if((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash-ne$file.sha256){throw '传感器负载摘要不匹配。'}}
foreach($required in @('SysBox.SensorHost.exe','SysBox.SensorHost.dll','LibreHardwareMonitorLib.dll')){if(-not$paths.Contains($required)){throw '传感器服务负载不完整。'}}
if($paths.Contains('.sysbox-service')-or$paths.Contains('sensors-files.json')){throw '传感器负载使用了保留的元数据路径。'}
Add-Type -AssemblyName System.Windows.Forms
$message="允许 SystemBox 在开机后持续读取 CPU 温度和主板风扇 RPM，用于水冷屏显示？`n用户 SID：$UserSid`n允许的界面：$client`n服务账户：LocalSystem；程序和配置保存在管理员保护目录。`n仅固定的温度/转速读取，不提供风扇控制、超频、任意命令或文件扫描。需要已安装的 PawnIO 驱动。`n停止同步或退出界面后会结束该轮采集；可随时停用服务。"
if([Windows.Forms.MessageBox]::Show($message,'AbsSystemBox 持久传感器采集授权','YesNo','Warning','Button2')-ne'Yes'){return}
Check-Trusted (Split-Path $target -Parent);Check-Trusted $target;Check-Trusted $policyRoot;Check-Trusted $policyDirectory
$policyPath=Join-Path $policyDirectory 'grants.json';Check-Trusted $policyPath
$service=Get-Service -Name 'AbsSystemBox.Sensors' -ErrorAction SilentlyContinue
if(($service-or(Test-Path -LiteralPath $target))-and(-not(Test-Path -LiteralPath (Join-Path $target '.sysbox-service'))-or[IO.File]::ReadAllText((Join-Path $target '.sysbox-service')).Trim()-ne$product)){throw '存在其他服务或目录，已保留原内容。'}
$registration=Get-CimInstance Win32_Service -Filter "Name='AbsSystemBox.Sensors'"
if($registration-and($registration.StartName-notin@('LocalSystem','NT AUTHORITY\SYSTEM')-or$registration.PathName.Trim()-ne('"'+(Join-Path $target 'SysBox.SensorHost.exe')+'" --service'))){throw '现有服务注册与预期不符，已保留原配置。'}
$existingBinary=Join-Path $target 'SysBox.SensorHost.exe'
if(Test-Path -LiteralPath $existingBinary){Check-Trusted $existingBinary;if([version][Diagnostics.FileVersionInfo]::GetVersionInfo($existingBinary).FileVersion-gt[version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'SysBox.SensorHost.exe')).FileVersion){throw '不允许将传感器服务降级。'}}
Protect (Split-Path $target -Parent);Protect $policyRoot;Protect $policyDirectory
$stamp=[guid]::NewGuid().ToString('N');$incoming=$target+'.incoming-'+$stamp;$previous=$target+'.previous-'+$stamp;$policyBackup=$policyPath+'.previous-'+$stamp
$productDirectory=[IO.Path]::GetFullPath((Split-Path $target -Parent)).TrimEnd('\')+'\'
foreach($ownedPath in @($target,$incoming,$previous)){if(-not[IO.Path]::GetFullPath($ownedPath).StartsWith($productDirectory,[StringComparison]::OrdinalIgnoreCase)){throw '传感器服务维护路径超出受保护产品目录。'}}
Protect $incoming
foreach($file in $manifest.files){$destination=[IO.Path]::GetFullPath((Join-Path $incoming $file.path));if(-not$destination.StartsWith($incoming+'\',[StringComparison]::OrdinalIgnoreCase)){throw '传感器负载目标路径无效。'};[IO.Directory]::CreateDirectory((Split-Path $destination -Parent))|Out-Null;Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $destination;Seal-File $destination;if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash-ne$file.sha256){throw '暂存的传感器服务负载摘要不匹配。'}}
Copy-Item -LiteralPath (Join-Path $source 'sensors-files.json') -Destination $incoming
[IO.File]::WriteAllText((Join-Path $incoming '.sysbox-service'),$product)
Seal-File (Join-Path $incoming 'sensors-files.json');Seal-File (Join-Path $incoming '.sysbox-service')
$directoryQueue=[Collections.Generic.Stack[string]]::new();$directoryQueue.Push($incoming)
while($directoryQueue.Count){$directory=$directoryQueue.Pop();Protect $directory;foreach($child in Get-ChildItem -LiteralPath $directory -Directory -Force){Check-NoReparse $child.FullName;$directoryQueue.Push($child.FullName)}}
$grants=@()
if(Test-Path -LiteralPath $policyPath){if((Get-Item -LiteralPath $policyPath).Length-gt65536){throw '传感器授权配置过大。'};$old=Get-Content $policyPath -Raw -Encoding UTF8|ConvertFrom-Json;if($old.Version-ne1){throw '传感器授权配置版本不受支持。'};$grants=@($old.Grants|Where-Object{$_.Sid-ne$UserSid-or$_.Image-ne$client})}
$grants+=@{Sid=$UserSid;Image=$client};if($grants.Count-gt32){throw '传感器授权用户数量超过上限。'}
$json=@{Version=1;Grants=@($grants)}|ConvertTo-Json -Depth 6
$policyIncoming=$policyPath+'.incoming-'+$stamp
[IO.File]::WriteAllText($policyIncoming,$json,[Text.UTF8Encoding]::new($false))
Seal-File $policyIncoming
$switched=$false;$policySwitched=$false;$hadTarget=Test-Path -LiteralPath $target;$hadPolicy=Test-Path -LiteralPath $policyPath
try {
if($service){Stop-Service -Name 'AbsSystemBox.Sensors';$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}
if($hadTarget){[IO.Directory]::Move($target,$previous)}
try{[IO.Directory]::Move($incoming,$target);$switched=$true}catch{if($hadTarget){[IO.Directory]::Move($previous,$target)};throw}
if($hadPolicy){[IO.File]::Replace($policyIncoming,$policyPath,$policyBackup)}else{[IO.File]::Move($policyIncoming,$policyPath)};$policySwitched=$true
$binary='"'+(Join-Path $target 'SysBox.SensorHost.exe')+'" --service'
if($service){& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') config 'AbsSystemBox.Sensors' 'binPath=' $binary 'start=' 'auto' 'obj=' 'LocalSystem'}else{& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') create 'AbsSystemBox.Sensors' 'binPath=' $binary 'start=' 'auto' 'obj=' 'LocalSystem' 'DisplayName=' 'AbsSystemBox 传感器读取服务'}
if($LASTEXITCODE-ne0){throw '传感器服务注册失败。'}
Start-Service -Name 'AbsSystemBox.Sensors';(Get-Service 'AbsSystemBox.Sensors').WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
} catch {
    $setupError=$_
    try {
        $failedService=Get-Service 'AbsSystemBox.Sensors' -ErrorAction SilentlyContinue
        if($failedService-and$failedService.Status-ne'Stopped'){Stop-Service 'AbsSystemBox.Sensors';$failedService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}
        if($switched){[IO.Directory]::Move($target,$incoming);if($hadTarget){[IO.Directory]::Move($previous,$target)}}
        if($policySwitched){if($hadPolicy){[IO.File]::Replace($policyBackup,$policyPath,$policyIncoming)}else{[IO.File]::Move($policyPath,$policyIncoming)}}
        if($service){Start-Service 'AbsSystemBox.Sensors'}elseif($failedService){& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete 'AbsSystemBox.Sensors'|Out-Null}
    } catch {Write-Warning '回滚需要管理员检查；原服务负载和授权配置备份已保留。'}
    throw $setupError
}
Write-Output ('原服务负载和授权配置已保留，可用于回滚：'+$previous+'; '+$policyBackup)
Write-Output '传感器服务已安装；未启动卷扫描或风扇调速。'
} finally {$setupLock.ReleaseMutex();$setupLock.Dispose()}
