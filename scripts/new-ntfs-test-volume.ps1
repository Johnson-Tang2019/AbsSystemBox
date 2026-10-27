# SPDX-License-Identifier: GPL-3.0-or-later
# Review before running: creates/formats only a NEW dedicated 512 MiB VHDX.
# Never use this script for an existing image or physical disk.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$folder = Join-Path $root 'artifacts/ntfs-lab'
$image = [IO.Path]::GetFullPath((Join-Path $folder 'sysbox-test-512mb.vhdx'))
if (-not $image.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Image outside workspace' }
if ($image.Contains('"') -or $image.Contains("`n") -or $image.Contains("`r")) { throw 'Invalid image path' }
if (Test-Path -LiteralPath $image) { throw 'Image already exists; refusing to overwrite or reformat it' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Dedicated test setup needs an administrator; normal application does not' }
[IO.Directory]::CreateDirectory($folder) | Out-Null
$commands = Join-Path $folder 'create-vhd.txt'
@("create vdisk file=`"$image`" maximum=512 type=expandable", "select vdisk file=`"$image`"", 'attach vdisk') |
    Set-Content -LiteralPath $commands -Encoding ASCII
$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/diskpart.exe') -ArgumentList @('/s', ('"' + $commands + '"')) -WindowStyle Hidden -Wait -PassThru `
    -RedirectStandardOutput (Join-Path $folder 'diskpart-output.txt') -RedirectStandardError (Join-Path $folder 'diskpart-errors.txt')
if ($process.ExitCode -ne 0) { throw 'VHD creation/attachment failed; inspect logs, image is preserved' }
$diskImage = Get-DiskImage -ImagePath $image -ErrorAction Stop
$disk = $diskImage | Get-Disk -ErrorAction Stop
if (-not $diskImage.Attached -or [int]$disk.BusType -ne 15 -or $disk.Size -ne 536870912 -or [int]$disk.PartitionStyle -ne 0 -or $disk.IsBoot -or $disk.IsSystem) {
    throw 'New blank file-backed image identity did not match; refusing to initialize'
}
# Only this verified, newly-created RAW virtual disk may be initialized.
$disk | Initialize-Disk -PartitionStyle GPT -ErrorAction Stop
$partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter -ErrorAction Stop
$again = Get-DiskImage -ImagePath $image | Get-Disk
if ($again.Number -ne $partition.DiskNumber -or [int]$again.BusType -ne 15 -or $again.Size -ne 536870912 -or $again.IsBoot -or $again.IsSystem) { throw 'Virtual disk identity changed; refusing to format' }
$volume = Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'SysBox_NtfsLab' -Confirm:$false -Force -ErrorAction Stop
if ($volume.FileSystem -ne 'NTFS' -or $volume.FileSystemLabel -ne 'SysBox_NtfsLab') { throw 'Unexpected test filesystem' }
$driveRoot = [string]$partition.DriveLetter + ':\'
$marker = [guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $driveRoot 'sysbox-lab-owner.txt'),$marker)
# Journal creation is confined to the NEW test volume; never run against a real user volume.
& (Join-Path $env:SystemRoot 'System32/fsutil.exe') usn createjournal m=33554432 a=4194304 ([string]$partition.DriveLetter + ':')
if ($LASTEXITCODE -ne 0) { throw 'Test journal creation failed' }
@{ imagePath=$image; driveRoot=$driveRoot; marker=$marker; sizeBytes=536870912; label='SysBox_NtfsLab' } |
    ConvertTo-Json | Set-Content (Join-Path $folder 'test-volume.json') -Encoding UTF8
Write-Output "Created isolated Windows NTFS test volume: $driveRoot"
Write-Output "Preserved image: $image"
# After testing: Dismount-DiskImage -ImagePath <the exact image above>. No automatic deletion.
