# SPDX-License-Identifier: GPL-3.0-or-later
param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/dotnet.ps1" restore SysBox.slnx --locked-mode
& "$PSScriptRoot/dotnet.ps1" test SysBox.slnx --configuration $Configuration --no-restore '-m:1'
