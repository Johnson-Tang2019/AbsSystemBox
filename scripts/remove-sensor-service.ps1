# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference='Stop'
if(-not[Environment]::Is64BitProcess){throw '请使用 64 位 Windows PowerShell 管理传感器服务。'}
if(-not([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '需要管理员授权。'}
$setupLock=[Threading.Mutex]::new($false,'Global\AbsSystemBox.Sensors.Setup')
if(-not$setupLock.WaitOne(0)){$setupLock.Dispose();throw '另一个传感器服务维护任务正在运行。'}
try {
$target=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AbsSystemBox/SensorService'
$registration=Get-CimInstance Win32_Service -Filter "Name='AbsSystemBox.Sensors'"
if(-not$registration){return}
if($registration.StartName-notin@('LocalSystem','NT AUTHORITY\SYSTEM')-or$registration.PathName.Trim()-ne('"'+(Join-Path $target 'SysBox.SensorHost.exe')+'" --service')){throw '现有服务注册与预期不符，已保留原配置。'}
if(-not(Test-Path -LiteralPath (Join-Path $target '.sysbox-service'))-or[IO.File]::ReadAllText((Join-Path $target '.sysbox-service')).Trim()-ne'AbsSystemBox.JohnsonTang2019.Sensors'){throw '服务所有权标记缺失，未作更改。'}
Add-Type -AssemblyName System.Windows.Forms
if([Windows.Forms.MessageBox]::Show('停止并取消注册 AbsSystemBox 的持久传感器读取服务？授权配置和服务文件会保留，CPU 与主板自动采集将停止。','移除可选服务','YesNo','Question','Button2')-ne'Yes'){return}
$service=Get-Service 'AbsSystemBox.Sensors' -ErrorAction SilentlyContinue
if($service){Stop-Service 'AbsSystemBox.Sensors';$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15));& (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete 'AbsSystemBox.Sensors';if($LASTEXITCODE-ne0){throw '传感器服务取消注册失败。'}}
} finally {$setupLock.ReleaseMutex();$setupLock.Dispose()}
