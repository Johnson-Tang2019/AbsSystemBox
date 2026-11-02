// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;
namespace SysBox.ProtocolTests;

public sealed class TransportFeaturesTests
{
    [Fact]
    public async Task FramingRejectsOversizeTruncationAndDuplicateRootFields()
    {
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new RpcRequest("1.0", "status", JsonSerializer.SerializeToElement(new
        {
        })), default);
        stream.Position = 0;
        Assert.Equal("status", (await Protocol.ReadAsync<RpcRequest>(stream, default)).Method);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, Protocol.MaxFrame + 1);
        await Assert.ThrowsAsync<IOException>(() => Protocol.ReadAsync<RpcRequest>(new MemoryStream(header), default));
        var duplicate = Encoding.UTF8.GetBytes("{\"version\":\"1.0\",\"method\":\"status\",\"method\":\"unsafe\",\"arguments\":{}}");
        using var bad = new MemoryStream();
        BinaryPrimitives.WriteInt32LittleEndian(header, duplicate.Length);
        bad.Write(header);
        bad.Write(duplicate);
        bad.Position = 0;
        await Assert.ThrowsAsync<IOException>(() => Protocol.ReadAsync<RpcRequest>(bad, default));
    }
    [Fact]
    public async Task AgentCannotApproveRulesEnableCleanupClearCacheOrInventMethods()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-Rpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY", root);
        Environment.SetEnvironmentVariable("SYSBOX_TEST_INDEX_DIRECTORY", Path.Combine(root, "index"));
        var pid = 0;
        try
        {
            var client = new RpcClient();
            var status = await client.CallAsync<JsonElement>("status", new
            {
            });
            pid = status.GetProperty("processId").GetInt32();
            Assert.Equal("user", status.GetProperty("mode").GetString());
            foreach (var method in new[] { "rules.approve", "rules.reject", "rules.rollback", "cleanup.configure", "index.clear", "host.stop" })
            {
                var error = await Assert.ThrowsAsync<SysBoxOperationException>(() => client.CallAsync<JsonElement>(method, new { approved = true, enabled = true, administrator = true }));
                Assert.Equal("ACCESS_DENIED", error.Error.Code);
            }
            Assert.Equal("METHOD_NOT_FOUND", (await Assert.ThrowsAsync<SysBoxOperationException>(() => client.CallAsync<JsonElement>("execute.anything", new { }))).Error.Code);
            Assert.False((await client.CallAsync<CleanupState>("cleanup.status", new
            {
            })).Policy.Enabled);
        }
        finally
        {
            // This PID belongs to the GUID-isolated host created by this test, never the user's normal host.
            if (pid > 0)
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                await process.WaitForExitAsync();
            }
            Environment.SetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX", null);
            Environment.SetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY", null);
            Environment.SetEnvironmentVariable("SYSBOX_TEST_INDEX_DIRECTORY", null);
            Directory.Delete(root, true);
        }
    }
}
public sealed class CleanupNativeFeaturesTests
{
    [Fact(Timeout = 200000)]
    public async Task OnlyTheExplicitRestrictedFixtureCanBeTrimmedAfterMinimumAge()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-Trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = Path.Combine(root, "ready.txt");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SysBox.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var exe = Path.Combine(directory.FullName, "tests/WorkingSetFixture/bin/Debug/net10.0-windows/WorkingSetFixture.exe");
        using var fixture = RestrictedUserLauncher.Launch(exe, "\"" + ready + "\"");
        try
        {
            for (var i = 0; i < 100 && !File.Exists(ready); i++)
                await Task.Delay(100);
            Assert.True(File.Exists(ready));
            var start = new DateTimeOffset(fixture.StartTime.ToUniversalTime());
            var early = SysBox.Native.WorkingSetAccess.Trim((uint)fixture.Id, start, "WorkingSetFixture.exe", 64 * 1048576);
            Assert.False(early.Success);
            Assert.Contains("120", early.Reason);
            await Task.Delay(TimeSpan.FromSeconds(122));
            var result = SysBox.Native.WorkingSetAccess.Trim((uint)fixture.Id, start, "WorkingSetFixture.exe", 64 * 1048576);
            Assert.True(result.Success, result.Reason);
            Assert.True(result.BeforeBytes >= 64 * 1048576);
            Assert.NotNull(result.AfterBytes);
            Assert.True(result.AfterBytes < result.BeforeBytes);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } Directory.Delete(root, true); }
    }
}
