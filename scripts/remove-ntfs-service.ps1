# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
if(-not[Environment]::Is64BitProcess){throw 'Use 64-bit Windows PowerShell for the x64 service'}
if(-not([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator approval is required'}
$setupLock=[Threading.Mutex]::new($false,'Global\AbsSystemBox.Ntfs.Setup')
if(-not$setupLock.WaitOne(0)){$setupLock.Dispose();throw 'Another service maintenance operation is active'}
try {
$target=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AbsSystemBox/NtfsService'
$registration=Get-CimInstance Win32_Service -Filter "Name='AbsSystemBox.Ntfs'"
if(-not$registration){return}
if($registration.StartName-notin@('LocalSystem','NT AUTHORITY\SYSTEM')-or$registration.PathName.Trim('"')-ne(Join-Path $target 'SysBox.NtfsService.exe')){throw 'Unexpected service registration; preserved'}
if(-not(Test-Path -LiteralPath (Join-Path $target '.sysbox-service'))-or[IO.File]::ReadAllText((Join-Path $target '.sysbox-service')).Trim()-ne'AbsSystemBox.JohnsonTang2019.Ntfs'){throw 'Owned service marker missing; nothing changed'}
Add-Type -AssemblyName System.Windows.Forms
if([Windows.Forms.MessageBox]::Show('停止并取消注册 AbsSystemBox 的只读 NTFS 服务？授权配置和服务文件会保留，不修改索引源文件。','移除可选服务','YesNo','Question','Button2')-ne'Yes'){return}
$service=Get-Service 'AbsSystemBox.Ntfs' -ErrorAction SilentlyContinue
if($service){Stop-Service 'AbsSystemBox.Ntfs';$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15));& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete 'AbsSystemBox.Ntfs';if($LASTEXITCODE-ne0){throw 'Service unregister failed'}}
} finally {$setupLock.ReleaseMutex();$setupLock.Dispose()}
