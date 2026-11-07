// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SysBox.NtfsBroker;

namespace SysBox.ProtocolTests;

public sealed class SensorServiceEndpointTests
{
    [Fact]
    public async Task AnAccessiblePipeCannotImpersonateTheRegisteredSystemService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(identity.User);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        var name = "SysBox.SensorEndpoint.Test." + Guid.NewGuid().ToString("N");
        await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024, acl);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connected;
        var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "SensorService", "SysBox.SensorHost.exe");
        Assert.Throws<UnauthorizedAccessException>(() => BrokerPeer.VerifyServiceServer(client, "AbsSystemBox.Sensors", image, "--service"));
    }
}
