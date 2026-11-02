// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
namespace SysBox.ProtocolTests;

public sealed class VolumeReadFeaturesTests
{
    [Fact]
    public async Task ReadOnlyCapacityEndpointReturnsRealVolumesAndDirectoryScopeCoverage()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-capacity-" + Guid.NewGuid().ToString("N"));
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
            var volumes = await client.CallAsync<VolumeCapacity[]>("volumes", new
            {
            });
            Assert.NotEmpty(volumes);
            Assert.Contains(volumes, v => v.TotalBytes > 0 && v.AvailableBytes >= 0 && v.AvailableBytes <= v.TotalBytes);
            Assert.All(volumes, v => Assert.Equal(0, v.IndexedScopeCount));
            Assert.All(volumes.Where(v => v.TotalBytes is not null), v => Assert.True(Directory.Exists(v.Root)));
            Assert.False(File.Exists(Path.Combine(root, "index", "metadata.db")));
        }
        finally
        {
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
