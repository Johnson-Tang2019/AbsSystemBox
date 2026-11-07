# SPDX-License-Identifier: GPL-3.0-or-later
param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/dotnet.ps1" restore SysBox.slnx --locked-mode
& "$PSScriptRoot/dotnet.ps1" build SysBox.slnx --configuration $Configuration --no-restore '-m:1'
& "$PSScriptRoot/dotnet.ps1" publish src/SysBox.SensorHost/SysBox.SensorHost.csproj --configuration $Configuration --no-restore '-m:1' --self-contained true "--output=$root/src/SysBox.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/sensors"
