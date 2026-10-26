# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[IO.Directory]::CreateDirectory((Join-Path $root '.tools')) | Out-Null
$archive = Join-Path $root '.tools/nsis-3.12.zip'
$expected = '56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f'
if (-not (Test-Path -LiteralPath $archive)) {
    foreach($url in @('https://downloads.sourceforge.net/project/nsis/NSIS%203/3.12/nsis-3.12.zip','https://distfiles.macports.org/nsis/nsis-3.12.zip')) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile "$archive.download"
            if((Get-FileHash -LiteralPath "$archive.download" -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected){Move-Item -LiteralPath "$archive.download" -Destination $archive;break}
        } catch { Write-Warning "NSIS mirror unavailable: $url" }
    }
}
if(-not (Test-Path -LiteralPath $archive)){throw 'No verified NSIS download'}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'NSIS SHA-256 mismatch' }
$compiler = Join-Path $root '.tools/nsis-3.12/makensis.exe'
if (-not (Test-Path -LiteralPath $compiler)) { Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root '.tools') }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'NSIS compiler missing' }
$compiler
