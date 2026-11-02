// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Principal;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.NtfsBroker;
using SysBox.Transport;
namespace SysBox.ProtocolTests;

public sealed class NtfsBrokerFeaturesTests
{
    [Fact]
    public async Task RealPipeCallerTokenIsVerifiedAndUntrustedImageCannotOpenAVolume()
    {
        var name = "SysBox-BrokerPeer-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connected;
        var writing = Protocol.WriteAsync(client, Request("journal", new
        {
            volume = Volume,
            approved = true
        }), deadline.Token);
        var request = await Protocol.ReadAsync<RpcRequest>(server, deadline.Token);
        await writing;
        var peer = BrokerPeer.Read(server, false);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User.Value, peer.Sid);
        Assert.Equal(Environment.ProcessPath, peer.Image);
        var opened = 0;
        var broker = new BrokerServer(new(1, [new(peer.Sid, @"C:\Approved\SysBox.CoreHost.exe", [Volume])]), _ => { opened++; throw new InvalidOperationException(); });
        Assert.Throws<UnauthorizedAccessException>(() => broker.Dispatch(peer, request, deadline.Token));
        Assert.Equal(0, opened);
    }
    private const string Volume = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
    private static RpcRequest Request(string method, object args) => new(Protocol.Version, method, JsonSerializer.SerializeToElement(args, Protocol.Json));
    [Fact]
    public void UnauthorizedSidImageVolumeAndDangerousMethodsNeverOpenAVolume()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User.Value;
        var image = Path.Combine(Path.GetTempPath(), "SysBox.CoreHost.exe");
        var opened = 0;
        var policy = new BrokerPolicy(1, [new(sid, image, [Volume])]);
        policy.Validate();
        var server = new BrokerServer(policy, _ => { opened++; throw new InvalidOperationException(); });
        var peer = new BrokerPeer(sid, 1, image);
        Assert.Throws<UnauthorizedAccessException>(() => server.Dispatch(peer with { Sid = "S-1-5-19" }, Request("journal", new { volume = Volume, approved = true }), default));
        Assert.Throws<UnauthorizedAccessException>(() => server.Dispatch(peer with { Image = Path.Combine(Path.GetTempPath(), "attacker.exe") }, Request("journal", new { volume = Volume }), default));
        Assert.Throws<UnauthorizedAccessException>(() => server.Dispatch(peer, Request("journal", new { volume = @"\\?\Volume{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}\" }), default));
        foreach (var name in new[] { "delete", "execute", "format", "trim", "grant", "service.start" })
            Assert.Equal("METHOD_NOT_FOUND", Assert.Throws<SysBoxOperationException>(() => server.Dispatch(peer, Request(name, new { volume = Volume, confirmed = true, administrator = true }), default)).Error.Code);
        Assert.Equal(0, opened);
        Assert.Equal(Volume, Assert.Single((string[])server.Dispatch(peer, Request("capabilities", new
        {
        }), default)));
    }
    [Fact]
    public void PipeAclRejectsNetworkAndDoesNotGrantEveryoneAccess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User.Value;
        var security = BrokerServer.Security(new(1, [new(sid, @"C:\SysBox.CoreHost.exe", [Volume])]));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
        Assert.DoesNotContain(rules, r => r.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.WorldSid));
        Assert.True(security.AreAccessRulesProtected);
    }
    [Fact]
    public void CanonicalIdsAndPathsRejectDeviceEscapesAndNormalizeV2Identity()
    {
        Assert.Equal(Volume, NtfsIdentity.Volume(Volume));
        Assert.Throws<ArgumentException>(() => NtfsIdentity.Volume(@"\\.\PhysicalDrive0"));
        Assert.Equal("0100000000000000", NtfsIdentity.FileId("01000000000000000000000000000000"));
        foreach (var path in new[] { @"\..\secret", @"\\server\share", @"\file:stream", @"C:\file" })
            Assert.Throws<ArgumentException>(() => NtfsIdentity.Relative(path));
    }
    [Fact]
    public async Task ForgedServicePipeIsRejectedBeforeSendingMetadataRequests()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var impostor = new NamedPipeServerStream(BrokerServer.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var connected = impostor.WaitForConnectionAsync(timeout.Token);
        var request = new BrokerClient().CallAsync<string[]>("capabilities", new
        {
        }, timeout.Token);
        await connected;
        var error = await Assert.ThrowsAsync<SysBoxOperationException>(() => request);
        Assert.Equal("NTFS_SERVICE_UNAVAILABLE", error.Error.Code);
        var buffer = new byte[1];
        Assert.Equal(0, await impostor.ReadAsync(buffer, timeout.Token));
    }
    [Fact]
    public async Task AgentFlagsCannotStartOrResumeNtfsAndMissingServiceKeepsOrdinaryMode()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-ntfs-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pid = 0;
        Environment.SetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY", root);
        Environment.SetEnvironmentVariable("SYSBOX_TEST_INDEX_DIRECTORY", Path.Combine(root, "index"));
        try
        {
            var client = new RpcClient();
            pid = (await client.CallAsync<JsonElement>("status", new
            {
            })).GetProperty("processId").GetInt32();
            foreach (var method in new[] { "ntfs.build", "ntfs.resume", "ntfs.stop" })
                Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<SysBoxOperationException>(() => client.CallAsync<JsonElement>(method, new { root = @"R:\", approved = true, isUi = true, administrator = true }))).Error.Code);
            Assert.Empty(await client.CallAsync<JsonElement[]>("ntfs.status", new
            {
            }));
            Assert.Equal("NTFS_SERVICE_UNAVAILABLE", (await Assert.ThrowsAsync<SysBoxOperationException>(() => client.CallAsync<string[]>("ntfs.capabilities", new { }))).Error.Code);
            Assert.False(File.Exists(Path.Combine(root, "index", "metadata.db")));
        }
        finally
        {
            if (pid > 0)
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill();
                await process.WaitForExitAsync();
            }
            foreach (var name in new[] { "SYSBOX_TEST_PIPE_SUFFIX", "SYSBOX_TEST_RULE_DIRECTORY", "SYSBOX_TEST_INDEX_DIRECTORY" })
                Environment.SetEnvironmentVariable(name, null);
            Directory.Delete(root, true);
        }
    }
}
