# SPDX-License-Identifier: GPL-3.0-or-later
param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug', [string] $Filter='')
$ErrorActionPreference = 'Stop'
if([string]::IsNullOrWhiteSpace($Filter)){Write-Output 'Reuse accepted reports; provide -Filter only for a new, unvalidated test group.';return}
& "$PSScriptRoot/dotnet.ps1" restore SysBox.slnx --locked-mode
& "$PSScriptRoot/dotnet.ps1" test SysBox.slnx --configuration $Configuration --no-restore '-m:1' --filter $Filter
