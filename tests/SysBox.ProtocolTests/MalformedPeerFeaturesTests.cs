// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SysBox.Transport;
namespace SysBox.ProtocolTests;

public sealed class MalformedPeerFeaturesTests
{
    [Fact]
    public async Task NullMethodDisconnectsOnlyItsRequestAndTheVerifiedHostRemainsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-null-method-" + Guid.NewGuid().ToString("N"));
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await pipe.ConnectAsync(timeout.Token);
                await Protocol.WriteAsync(pipe, new
                {
                    version = Protocol.Version,
                    method = (string)null,
                    arguments = new
                    {
                    }
                }, timeout.Token);
                await Assert.ThrowsAsync<EndOfStreamException>(() => Protocol.ReadAsync<RpcResponse>(pipe, timeout.Token));
            }
            Assert.Equal(pid, (await client.CallAsync<JsonElement>("status", new
            {
            })).GetProperty("processId").GetInt32());
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
