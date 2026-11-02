# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory=$true)][string]$UserSid,[Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z]:\\$')][string]$VolumeRoot,[switch]$ReviewOnly)
$ErrorActionPreference='Stop'
if(-not[Environment]::Is64BitProcess){throw 'Use 64-bit Windows PowerShell for the x64 service'}
$product='AbsSystemBox.JohnsonTang2019.Ntfs'
$target=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AbsSystemBox/NtfsService'
$policyRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AbsSystemBox'
$policyDirectory=Join-Path $policyRoot 'NtfsService'
$source=Join-Path (Split-Path $PSScriptRoot -Parent) 'ntfs-service'
$client=Join-Path (Split-Path $PSScriptRoot -Parent) 'host/SysBox.CoreHost.exe'
$admin=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$sid=[Security.Principal.SecurityIdentifier]::new($UserSid)
if($ReviewOnly){[pscustomobject]@{service='AbsSystemBox.Ntfs';account='LocalSystem';target=$target;userSid=$sid.Value;volume=$VolumeRoot;client=$client;operations='Read-only query/enumerate/journal/metadata only; no scan on installation'};return}
if(-not([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run the separate setup with administrator approval.'}
$setupLock=[Threading.Mutex]::new($false,'Global\AbsSystemBox.Ntfs.Setup')
if(-not$setupLock.WaitOne(0)){$setupLock.Dispose();throw 'Another service maintenance operation is active'}
try {
function Check-NoReparse([string]$Path){if(Test-Path -LiteralPath $Path){if((Get-Item -LiteralPath $Path -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation path rejected'}};for($item=[IO.DirectoryInfo]::new((Split-Path $Path -Parent));$item;$item=$item.Parent){if($item.Exists -and ($item.Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse installation path rejected'}}}
function Check-Trusted([string]$Path){
    Check-NoReparse $Path
    if(-not(Test-Path -LiteralPath $Path)){return}
    $acl=Get-Acl -LiteralPath $Path
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value-notin$trusted){throw 'Untrusted existing owner; preserved'}
    $write=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.PropagationFlags-band[Security.AccessControl.PropagationFlags]::InheritOnly){continue}
        if($rule.AccessControlType-eq'Allow'-and$rule.IdentityReference.Value-notin$trusted-and($rule.FileSystemRights-band$write)){throw 'Untrusted write access; preserved'}
    }
}
function Protect([string]$Path){
    Check-NoReparse $Path
    if(Test-Path -LiteralPath $Path){$owner=(Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]);if($owner.Value-notin@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')){throw 'Existing service directory has an untrusted owner; preserved'}}else{[IO.Directory]::CreateDirectory($Path)|Out-Null}
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
Add-Type -TypeDefinition 'using System;using System.Text;using System.Runtime.InteropServices;public static class SysBoxVolumeSetup{[DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool GetVolumeNameForVolumeMountPointW(string root,StringBuilder name,uint size);}'
$drive=[IO.DriveInfo]::new($VolumeRoot);if(-not$drive.IsReady-or$drive.DriveFormat-ne'NTFS'-or$drive.DriveType-notin@([IO.DriveType]::Fixed,[IO.DriveType]::Removable)){throw 'A ready local NTFS volume is required'}
$name=[Text.StringBuilder]::new(64);if(-not[SysBoxVolumeSetup]::GetVolumeNameForVolumeMountPointW($VolumeRoot,$name,64)){throw 'Volume GUID unavailable'}
if(-not(Test-Path -LiteralPath (Join-Path $source 'service-files.json'))-or-not(Test-Path -LiteralPath $client)){throw 'Use the complete installed/published application, not a source checkout'}
$manifest=Get-Content (Join-Path $source 'service-files.json') -Raw|ConvertFrom-Json
if($manifest.product-ne$product-or$manifest.files.Count-gt2000){throw 'Invalid service manifest'}
foreach($file in $manifest.files){if([IO.Path]::IsPathRooted($file.path)-or(($file.path-split'[/\\]')-contains'..')-or$file.path.Contains(':')){throw 'Invalid payload path'};$candidate=[IO.Path]::GetFullPath((Join-Path $source $file.path));if(-not$candidate.StartsWith([IO.Path]::GetFullPath($source).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload escaped source'};Check-NoReparse $candidate;if((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash-ne$file.sha256){throw 'Service payload hash mismatch'}}
Add-Type -AssemblyName System.Windows.Forms
$message="为当前用户授予整卷文件名、大小及硬链接元数据的只读访问？`n用户 SID：$UserSid`n卷：$VolumeRoot`n身份：$name`n调用程序：$client`n服务账户：LocalSystem；随 Windows 启动，但安装不会触发扫描。`n包括其他用户目录的元数据，不包含文件正文、删除或任意命令接口。"
if([Windows.Forms.MessageBox]::Show($message,'AbsSystemBox 可选 NTFS 服务','YesNo','Warning','Button2')-ne'Yes'){return}
Check-Trusted (Split-Path $target -Parent);Check-Trusted $target;Check-Trusted $policyRoot;Check-Trusted $policyDirectory
$policyPath=Join-Path $policyDirectory 'grants.json';Check-Trusted $policyPath
$service=Get-Service -Name 'AbsSystemBox.Ntfs' -ErrorAction SilentlyContinue
if(($service-or(Test-Path -LiteralPath $target))-and(-not(Test-Path -LiteralPath (Join-Path $target '.sysbox-service'))-or[IO.File]::ReadAllText((Join-Path $target '.sysbox-service')).Trim()-ne$product)){throw 'An unrelated service or directory exists; preserved'}
$registration=Get-CimInstance Win32_Service -Filter "Name='AbsSystemBox.Ntfs'"
if($registration-and($registration.StartName-notin@('LocalSystem','NT AUTHORITY\SYSTEM')-or$registration.PathName.Trim('"')-ne(Join-Path $target 'SysBox.NtfsService.exe'))){throw 'Unexpected service registration; preserved'}
$existingBinary=Join-Path $target 'SysBox.NtfsService.exe'
if(Test-Path -LiteralPath $existingBinary){Check-Trusted $existingBinary;if([version][Diagnostics.FileVersionInfo]::GetVersionInfo($existingBinary).FileVersion-gt[version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'SysBox.NtfsService.exe')).FileVersion){throw 'Service downgrade refused'}}
Protect (Split-Path $target -Parent);Protect $policyRoot;Protect $policyDirectory
$stamp=[guid]::NewGuid().ToString('N');$incoming=$target+'.incoming-'+$stamp;$previous=$target+'.previous-'+$stamp;$policyBackup=$policyPath+'.previous-'+$stamp
Protect $incoming
foreach($file in $manifest.files){$destination=[IO.Path]::GetFullPath((Join-Path $incoming $file.path));if(-not$destination.StartsWith($incoming+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid destination'};[IO.Directory]::CreateDirectory((Split-Path $destination -Parent))|Out-Null;Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $destination;Seal-File $destination;if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash-ne$file.sha256){throw 'Staged service payload hash mismatch'}}
Copy-Item -LiteralPath (Join-Path $source 'service-files.json') -Destination $incoming
[IO.File]::WriteAllText((Join-Path $incoming '.sysbox-service'),$product)
Seal-File (Join-Path $incoming 'service-files.json');Seal-File (Join-Path $incoming '.sysbox-service')
$directoryQueue=[Collections.Generic.Stack[string]]::new();$directoryQueue.Push($incoming)
while($directoryQueue.Count){$directory=$directoryQueue.Pop();Protect $directory;foreach($child in Get-ChildItem -LiteralPath $directory -Directory -Force){Check-NoReparse $child.FullName;$directoryQueue.Push($child.FullName)}}
$grants=@()
if(Test-Path -LiteralPath $policyPath){if((Get-Item -LiteralPath $policyPath).Length-gt65536){throw 'Policy too large'};$old=Get-Content $policyPath -Raw|ConvertFrom-Json;if($old.Version-ne1){throw 'Unknown policy version'};$grants=@($old.Grants|Where-Object{$_.Sid-ne$UserSid-or$_.Image-ne$client})}
$volumes=@($name.ToString());if($old){$volumes+=@($old.Grants|Where-Object{$_.Sid-eq$UserSid-and$_.Image-eq$client}|ForEach-Object{$_.Volumes})};$volumes=@($volumes|Sort-Object -Unique)
$grants+=@{Sid=$UserSid;Image=$client;Volumes=$volumes};if($grants.Count-gt32){throw 'Grant limit exceeded'}
$json=@{Version=1;Grants=@($grants)}|ConvertTo-Json -Depth 6
$policyIncoming=$policyPath+'.incoming-'+$stamp
[IO.File]::WriteAllText($policyIncoming,$json,[Text.UTF8Encoding]::new($false))
Seal-File $policyIncoming
$switched=$false;$policySwitched=$false;$hadTarget=Test-Path -LiteralPath $target;$hadPolicy=Test-Path -LiteralPath $policyPath
try {
if($service){Stop-Service -Name 'AbsSystemBox.Ntfs';$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}
if($hadTarget){[IO.Directory]::Move($target,$previous)}
try{[IO.Directory]::Move($incoming,$target);$switched=$true}catch{if($hadTarget){[IO.Directory]::Move($previous,$target)};throw}
if($hadPolicy){[IO.File]::Replace($policyIncoming,$policyPath,$policyBackup)}else{[IO.File]::Move($policyIncoming,$policyPath)};$policySwitched=$true
$binary='"'+(Join-Path $target 'SysBox.NtfsService.exe')+'"'
if($service){& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') config 'AbsSystemBox.Ntfs' 'binPath=' $binary 'start=' 'auto' 'obj=' 'LocalSystem'}else{& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') create 'AbsSystemBox.Ntfs' 'binPath=' $binary 'start=' 'auto' 'obj=' 'LocalSystem' 'DisplayName=' 'AbsSystemBox 只读 NTFS 服务'}
if($LASTEXITCODE-ne0){throw 'Service registration failed'}
Start-Service -Name 'AbsSystemBox.Ntfs';(Get-Service 'AbsSystemBox.Ntfs').WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
} catch {
    $setupError=$_
    try {
        $failedService=Get-Service 'AbsSystemBox.Ntfs' -ErrorAction SilentlyContinue
        if($failedService-and$failedService.Status-ne'Stopped'){Stop-Service 'AbsSystemBox.Ntfs';$failedService.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}
        if($switched){[IO.Directory]::Move($target,$incoming);if($hadTarget){[IO.Directory]::Move($previous,$target)}}
        if($policySwitched){if($hadPolicy){[IO.File]::Replace($policyBackup,$policyPath,$policyIncoming)}else{[IO.File]::Move($policyPath,$policyIncoming)}}
        if($service){Start-Service 'AbsSystemBox.Ntfs'}elseif($failedService){& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete 'AbsSystemBox.Ntfs'|Out-Null}
    } catch {Write-Warning 'Rollback needs administrator review; original payload/policy backups were preserved.'}
    throw $setupError
}
Write-Output ('Previous owned payload and policy are preserved for rollback: '+$previous+'; '+$policyBackup)
Write-Output 'Read-only service installed; no volume enumeration was started.'
} finally {$setupLock.ReleaseMutex();$setupLock.Dispose()}
