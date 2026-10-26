# SPDX-License-Identifier: GPL-3.0-or-later
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$local = Join-Path $root '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $local) { $local } else { 'dotnet' }
if (Test-Path -LiteralPath $local) {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli-home'
    $env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
}
Push-Location $root
try {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
