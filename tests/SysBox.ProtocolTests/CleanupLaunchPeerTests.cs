// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using SysBox.Transport;

namespace SysBox.ProtocolTests;

public sealed class CleanupLaunchPeerTests
{
    [Fact]
    public async Task ChecksLocalClientAgainstProcessAndImage()
    {
        var name = "AbsSystemBox.Cleanup.Test." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connection;

        using var process = Process.GetCurrentProcess();
        var image = Environment.ProcessPath ?? throw new InvalidOperationException();
        PeerIdentity.VerifyLaunchedClient(server, process, image);
        Assert.Throws<UnauthorizedAccessException>(() => PeerIdentity.VerifyLaunchedClient(server, process, image + ".wrong"));
        using var wrongProcess = Process.GetProcessById(0);
        Assert.Throws<UnauthorizedAccessException>(() => PeerIdentity.VerifyLaunchedClient(server, wrongProcess, image));
    }
}
