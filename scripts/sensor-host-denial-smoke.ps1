# SPDX-License-Identifier: GPL-3.0-or-later
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $root 'src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/sensors/SysBox.SensorHost.exe'
$nonce = [guid]::NewGuid().ToString('N')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $acl = [IO.Pipes.PipeSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($identity.User)
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::NetworkSid, $null), [IO.Pipes.PipeAccessRights]::FullControl, [Security.AccessControl.AccessControlType]::Deny))
    $acl.AddAccessRule([IO.Pipes.PipeAccessRule]::new($identity.User, [IO.Pipes.PipeAccessRights]::FullControl, [Security.AccessControl.AccessControlType]::Allow))
    $pipe = [IO.Pipes.NamedPipeServerStreamAcl]::Create('AbsSystemBox.Sensors.' + $nonce, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous, 16384, 16384, $acl)
    try {
        # This server is PowerShell, not the trusted UI image. The helper must reject it before sampling.
        $process = Start-Process -FilePath $executable -ArgumentList $nonce -WindowStyle Hidden -PassThru
        try {
            if (-not $pipe.WaitForConnectionAsync().Wait(5000)) { throw 'Helper did not connect for identity validation' }
            if (-not $process.WaitForExit(5000)) { throw 'Helper accepted or stalled on an untrusted parent image' }
            if ($pipe.ReadByte() -ne -1) { throw 'Helper disclosed data to an untrusted parent image' }
            $report = @{untrustedParentRejected=$true;exitCode=$process.ExitCode;sensorDataDisclosed=$false;nativeConsentAutomated=$false}
            [IO.Directory]::CreateDirectory((Join-Path $root 'artifacts/tests/cooling-display')) | Out-Null
            $report | ConvertTo-Json | Set-Content (Join-Path $root 'artifacts/tests/cooling-display/peer-denial.json') -Encoding UTF8
            $report | ConvertTo-Json -Compress
        } finally { $process.Dispose() }
    } finally { $pipe.Dispose() }
} finally { $identity.Dispose() }
