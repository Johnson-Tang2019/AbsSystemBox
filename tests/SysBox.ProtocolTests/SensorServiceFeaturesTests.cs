// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.NtfsBroker;
using SysBox.SensorHost;
using SysBox.Transport;

namespace SysBox.ProtocolTests;

public sealed class SensorServiceFeaturesTests
{
    private const string Sid = "S-1-5-21-11111111-22222222-33333333-1001";
    private const string Image = @"C:\Users\Fixture\Programs\AbsSystemBox\app\SysBox.App.exe";
    private static RpcRequest Request(string method, object arguments) => new(Protocol.Version, method, JsonSerializer.SerializeToElement(arguments, Protocol.Json));

    [Fact]
    public void OnlyGrantedInteractiveUiCanRequestFixedReadAndAgentFlagsDoNotExpandAuthority()
    {
        var policy = new SensorServicePolicy(1, [new(Sid, Image)]);
        policy.Validate();
        var peer = new BrokerPeer(Sid, 1, Image);
        policy.ValidateRequest(peer, Request("cooling.sensors.read", new { }));
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateRequest(peer with { Sid = "S-1-5-18" }, Request("cooling.sensors.read", new { approved = true, administrator = true })));
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateRequest(peer with { Session = 0 }, Request("cooling.sensors.read", new { })));
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateRequest(peer with { Image = @"C:\Other\SysBox.App.exe" }, Request("cooling.sensors.read", new { })));
        foreach (var method in new[] { "execute", "fan.set", "sensor.configure", "cooling.sensors.grant", "service.start" })
            Assert.Throws<SysBoxOperationException>(() => policy.ValidateRequest(peer, Request(method, new { approved = true, command = "arbitrary" })));
        Assert.Throws<SysBoxOperationException>(() => policy.ValidateRequest(peer, Request("cooling.sensors.read", new { privileged = true })));
        Assert.Throws<SysBoxOperationException>(() => policy.ValidateRequest(peer, Request("cooling.sensors.read", new[] { "CPU" })));
        Assert.Throws<SysBoxOperationException>(() => policy.ValidateRequest(peer, Request("cooling.sensors.read", new { }) with { Version = "future" }));
    }

    [Fact]
    public void PersistentPolicyRejectsForeignExecutablesNetworkPathsSystemUsersAndUnknownVersions()
    {
        foreach (var image in new[] { @"C:\SysBox.CoreHost.exe", @"\\server\share\SysBox.App.exe", @"C:\Allowed\SysBox.App.exe:stream", "SysBox.App.exe" })
            Assert.Throws<IOException>(() => new SensorServicePolicy(1, [new(Sid, image)]).Validate());
        Assert.Throws<IOException>(() => new SensorServicePolicy(2, [new(Sid, Image)]).Validate());
        Assert.Throws<IOException>(() => new SensorServicePolicy(1, []).Validate());
        Assert.Throws<IOException>(() => new SensorServicePolicy(1, [new("S-1-5-18", Image)]).Validate());
        Assert.Throws<IOException>(() => new SensorServicePolicy(1, Enumerable.Repeat(new SensorServiceGrant(Sid, Image), 33).ToArray()).Validate());
    }

    [Fact]
    public void ServicePipeAclIsProtectedRejectsNetworkAndGrantsOnlyConfiguredUserAndSystem()
    {
        var acl = new SensorServicePolicy(1, [new(Sid, Image)]).Security();
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        Assert.Contains(rules, rule => rule.AccessControlType == AccessControlType.Deny && ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid));
        var allowed = rules.Where(rule => rule.AccessControlType == AccessControlType.Allow).Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).ToArray();
        Assert.Equal(new[] { "S-1-5-18", Sid }, allowed.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(rules, rule => rule.AccessControlType == AccessControlType.Allow && ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.WorldSid));
    }
}
