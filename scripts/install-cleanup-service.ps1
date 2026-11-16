# SPDX-License-Identifier: GPL-3.0-or-later
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^S-1-[0-9-]+$')][string]$UserSid,
    [ValidatePattern('^[0-9]+(,[0-9]+)*$')][string]$Areas,
    [switch]$Revoke
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Environment]::Is64BitProcess -or -not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员权限。' }
$name = 'AbsSystemBox.CleanupService'
$parent = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AbsSystemBox'
$target = Join-Path $parent 'CleanupService'
$image = Join-Path $target 'SysBox.CleanupHelper.exe'
$command = '"' + $image + '" --service'
$policy = Join-Path $target 'grant.json'
$app = Split-Path $PSScriptRoot -Parent
$client = Join-Path $app 'SysBox.App.exe'
$sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
$admin = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$trusted = @($admin.Value,$system.Value,'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

function Assert-Protected([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '管理路径不能经过重解析点。' }
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw '管理路径所有者不可信。' }
    $write = [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trusted -and
            -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($rule.FileSystemRights -band $write)) { throw '管理路径允许普通用户写入。' }
    }
}

function New-ProtectedDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Assert-Protected $Path; return }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($admin)
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($principal in @($admin,$system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    $directory = [IO.DirectoryInfo]::new($Path)
    $directory.Create($acl)
    Assert-Protected $Path
}

function Seal-File([string]$Path) {
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($admin)
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($principal in @($admin,$system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,'FullControl','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-Protected $Path
}

$lock = [Threading.Mutex]::new($false,'Global\AbsSystemBox.CleanupService.Setup')
$entered = $false
try {
    try { $entered = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $entered = $true }
    if (-not $entered) { throw '已有清理授权管理任务。' }
    $installed = Get-ItemProperty -LiteralPath "Registry::HKEY_USERS\$UserSid\Software\Microsoft\Windows\CurrentVersion\Uninstall\AbsSystemBox.JohnsonTang2019"
    if (-not [string]::Equals($client, (Join-Path $installed.InstallLocation 'app\SysBox.App.exe'), [StringComparison]::OrdinalIgnoreCase)) { throw '请从已安装的 SystemBox 发起授权。' }
    Assert-Protected ([Environment]::GetFolderPath('ProgramFiles'))
    if (Test-Path -LiteralPath $parent) { Assert-Protected $parent }
    if (Test-Path -LiteralPath $target) { Assert-Protected $target }
    $service = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($service -and ($service.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM') -or $service.PathName -ne $command)) { throw '同名清理服务的身份不符。' }
    $priorGrant = $null
    if (Test-Path -LiteralPath $policy) {
        Assert-Protected $policy
        $priorGrant = Get-Content -LiteralPath $policy -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($priorGrant.Sid -ne $sid.Value) { throw '此授权属于另一个账户。' }
    }
    Add-Type -AssemblyName System.Windows.Forms
    if ($Revoke) {
        if (-not $priorGrant) { return }
        if ([Windows.Forms.MessageBox]::Show('撤销本账户的悬浮球直接清理授权？','AbsSystemBox 清理授权','YesNo','Question','Button2') -ne 'Yes') { exit 2 }
        if ($service) {
            Set-Service -Name $name -StartupType Disabled
            Stop-Service -Name $name
            (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
            & (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete $name | Out-Null
            if ($LASTEXITCODE -ne 0) { throw '无法注销清理服务。' }
        }
        Remove-Item -LiteralPath $policy
        return
    }
    $labels = @('全局工作集','逐进程工作集','系统文件缓存','修改的文件缓存','修改页面','优先级 0 待机列表','完整待机列表','内存合并','注册表缓存')
    $selected = @($Areas -split ',' | ForEach-Object {[int]$_} | Sort-Object -Unique)
    if ($selected.Count -lt 1 -or @($selected | Where-Object {$_ -lt 0 -or $_ -ge $labels.Count -or $_ -eq 1}).Count) { throw '清理清单无效或包含逐进程工作集。' }
    $message = "授权此账户双击悬浮球时直接执行以下项目？`n`n" + (($selected | ForEach-Object {$labels[$_]}) -join "`n") +
        "`n`n清单会保存在管理员保护目录中，可在内存页撤销。部分操作可能造成短暂停顿或重新调页，全局工作集不保护前台进程。"
    if ([Windows.Forms.MessageBox]::Show($message,'AbsSystemBox 悬浮球清理授权','YesNo','Warning','Button2') -ne 'Yes') { exit 2 }
    New-ProtectedDirectory $parent
    New-ProtectedDirectory $target
    if (-not $service) {
        $source = Join-Path $app 'cleanup'
        $manifest = Join-Path $source 'cleanup-files.json'
        if ((Get-Item -LiteralPath $manifest).Length -gt 262144) { throw '清理组件清单过大。' }
        $files = @((Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json).files)
        if ($files.Count -lt 5 -or $files.Count -gt 500 -or 'SysBox.CleanupHelper.exe' -notin $files.name) { throw '清理组件不完整。' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $files) {
            if ($entry.name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]*\.(exe|dll|json)$' -or
                $entry.name -in @('grant.json','cleanup-files.json','appsettings.json','appsettings.Production.json') -or
                $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not $seen.Add($entry.name)) { throw '清理组件清单无效。' }
            $file = Get-Item -LiteralPath (Join-Path $source $entry.name)
            if ($file.PSIsContainer -or $file.Length -gt 67108864 -or $file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '清理组件路径无效。' }
            $destination = Join-Path $target $entry.name
            if (Test-Path -LiteralPath $destination) { Assert-Protected $destination }
            [IO.File]::Copy($file.FullName,$destination,$true)
            Seal-File $destination
            if ((Get-FileHash -LiteralPath $destination).Hash -ne $entry.sha256) { throw '清理组件校验失败。' }
        }
    }
    Assert-Protected $image
    $temporary = Join-Path $target ('grant-' + [guid]::NewGuid().ToString('N') + '.json')
    @{Sid=$sid.Value;Image=$client;Areas=$selected} | ConvertTo-Json -Compress | Set-Content -LiteralPath $temporary -Encoding UTF8
    Seal-File $temporary
    if ($service) { Stop-Service -Name $name; (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15)) }
    if (Test-Path -LiteralPath $policy) { [IO.File]::Replace($temporary,$policy,$null) } else { [IO.File]::Move($temporary,$policy) }
    Assert-Protected $policy
    if ($service) { Set-Service -Name $name -StartupType Automatic }
    else { New-Service -Name $name -BinaryPathName $command -DisplayName 'AbsSystemBox Memory Cleanup' -StartupType Automatic | Out-Null }
    Start-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
} finally {
    if ($entered) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
