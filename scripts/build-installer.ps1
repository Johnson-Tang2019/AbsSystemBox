# SPDX-License-Identifier: GPL-3.0-or-later
param([string] $Version='', [string] $WindowsVersion='', [string] $OutputRoot='', [switch] $AllowDirty)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[xml]$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
if(-not $Version){$Version=[string]$props.Project.PropertyGroup.Version}
if(-not $WindowsVersion){$WindowsVersion=[string]$props.Project.PropertyGroup.FileVersion}
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$' -or $WindowsVersion -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw 'Invalid release version'}
$parsedVersion=[version]$WindowsVersion
if(@($parsedVersion.Major,$parsedVersion.Minor,$parsedVersion.Build,$parsedVersion.Revision | Where-Object {$_ -gt 65535}).Count){throw 'Windows version components exceed 65535'}
$revision=(& git -C $root rev-parse HEAD).Trim()
$dirty= -not [string]::IsNullOrWhiteSpace((& git -C $root status --porcelain | Out-String))
if($dirty -and -not $AllowDirty){throw 'Commit source before creating release assets; use -AllowDirty only for local installer validation'}
if(-not $OutputRoot){$OutputRoot=Join-Path $root 'artifacts/releases'}
$release=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $Version
$stage=Join-Path $release 'payload'
if(Test-Path -LiteralPath $stage){throw "Refusing to reuse a potentially stale payload: $stage"}
[IO.Directory]::CreateDirectory($release) | Out-Null
$compiler=& (Join-Path $PSScriptRoot 'get-nsis.ps1')
& (Join-Path $PSScriptRoot 'dotnet.ps1') restore SysBox.slnx --locked-mode
& (Join-Path $PSScriptRoot 'dotnet.ps1') clean src/SysBox.App/SysBox.App.csproj -c Release '-p:Platform=x64' '-v:q'
& (Join-Path $PSScriptRoot 'dotnet.ps1') publish src/SysBox.App/SysBox.App.csproj -c Release --no-restore '-m:1' '-p:Platform=x64' "-p:Version=$Version" "-p:FileVersion=$WindowsVersion" "-p:SourceRevisionId=$revision" '-p:DebugType=None' '-p:DebugSymbols=false' "--output=$stage"
foreach($resource in @('App.xbf','MainWindow.xbf','SysBox.App.pri','Themes/PinkTheme.xbf','Controls/BatteryCard.xbf','Views/RulesPage.xbf','Views/SearchPage.xbf','Views/StoragePage.xbf','Controls/StorageTreemap.xbf')){
    if(-not (Test-Path -LiteralPath (Join-Path $stage $resource))){throw "Published WinUI resource missing: $resource"}
}
foreach($name in @('LICENSE','COPYING.md','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $stage -Recurse
[IO.Directory]::CreateDirectory((Join-Path $stage 'docs')) | Out-Null
foreach($name in @('RULES.md','BATTERY.md','INSTALL.md')){Copy-Item -LiteralPath (Join-Path $root "docs/$name") -Destination (Join-Path $stage 'docs')}
$files=@(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative=$_.FullName.Substring($stage.Length+1)
    if($relative.Length -gt 800 -or $relative.Contains("`n") -or $relative.Contains("`r")){throw 'Unsafe installer manifest path'}
    [ordered]@{path=$relative.Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
if($files.Count -gt 5000){throw 'Installer payload exceeds manifest limit'}
$manifest=Join-Path $release 'installed-files.txt'
[IO.File]::WriteAllLines($manifest,@($files | ForEach-Object {$_.path.Replace('/','\')}),[Text.UTF8Encoding]::new($false))
$licenseText="AbsSystemBox $Version`r`n项目源码 GPL-3.0-or-later；第三方组件保留各自许可，未重新授权为 GPL。`r`n第三方许可原文同时安装于 app/licenses；微软组件按其独立条款使用和分发。`r`n`r`n" + [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$seen=[Collections.Generic.HashSet[string]]::new()
foreach($file in (Get-ChildItem (Join-Path $root 'licenses') -File -Recurse | Where-Object {$_.Name -match '^(license|copying|sdk_license)'})){
    $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if($seen.Add($hash)){$licenseText+="`r`n`r`n===== $($file.FullName.Substring($root.Length+1)) =====`r`n" + [IO.File]::ReadAllText($file.FullName)}
}
$licenseFile=Join-Path $release 'installer-license.txt'
[IO.File]::WriteAllText($licenseFile,$licenseText,[Text.UTF8Encoding]::new($true))
$installer=Join-Path $release "AbsSystemBox-$Version-win-x64-setup.exe"
& $compiler '/INPUTCHARSET' 'UTF8' '/V3' "/DVERSION=$Version" "/DWINDOWS_VERSION=$WindowsVersion" "/DAPPDIR=$stage" "/DMANIFEST=$manifest" "/DLICENSEFILE=$licenseFile" "/DOUTPUT=$installer" (Join-Path $root 'packaging/installer.nsi')
if($LASTEXITCODE -ne 0){throw "Installer compiler failed: $LASTEXITCODE"}
$report=[ordered]@{version=$Version;windowsVersion=$WindowsVersion;sourceRevision=$revision;sourceDirty=$dirty;platform='win-x64';selfContained=$true;signed=$false;files=$files;installerSha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'manifest.json') -Encoding UTF8
if(-not $dirty){
    & git -C $root archive --format=zip "--prefix=AbsSystemBox-$Version/" -o (Join-Path $release "AbsSystemBox-$Version-source.zip") $revision
    if($LASTEXITCODE -ne 0){throw 'Source archive failed'}
}
$sums=@(Get-ChildItem -LiteralPath $release -File | Where-Object {$_.Name -match '\.(exe|zip|json)$'} | ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"})
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'),$sums,[Text.UTF8Encoding]::new($false))
$installer
