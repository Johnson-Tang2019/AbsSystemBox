// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;
using SysBox.NtfsBroker;

namespace SysBox.App.Services;

public sealed class WindowsCoolingSensorClient : ICoolingSensorSource, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private PipeStream? pipe;
    private Process? process;
    private CancellationTokenSource? session;

    public async Task AuthorizeAsync(CancellationToken token)
    {
        Stop();
        await gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "install-sensor-service.ps1");
            if (!File.Exists(script)) throw new IOException("后台采集安装工具缺失，请使用完整安装版。");
            using var identity = WindowsIdentity.GetCurrent();
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-UserSid", identity.User!.Value }) start.ArgumentList.Add(argument);
            using var setup = Process.Start(start) ?? throw new IOException("无法启动后台采集授权工具。");
            await setup.WaitForExitAsync(deadline.Token);
            if (setup.ExitCode != 0) throw new IOException("后台采集服务安装失败，原配置已保留。");
            if (!await ConnectServiceAsync(deadline.Token)) throw new IOException("未安装后台采集服务或已取消授权。");
            _ = await ReadConnectedAsync(deadline.Token);
        }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }

    public async Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            if (pipe is null || !pipe.IsConnected)
                if (!await ConnectServiceAsync(deadline.Token)) await StartAsync(false, deadline.Token);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, session!.Token);
            return await ReadConnectedAsync(combined.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Stop();
            throw new IOException("硬件采集超时或已停止，请重新检测。");
        }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }

    private async Task<bool> ConnectServiceAsync(CancellationToken token)
    {
        Stop();
        var connection = new NamedPipeClientStream(".", "AbsSystemBox.Sensors.Service.v1", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            try { await connection.ConnectAsync(1500, token); }
            catch (TimeoutException) { connection.Dispose(); return false; }
            var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "SensorService", "SysBox.SensorHost.exe");
            BrokerPeer.VerifyServiceServer(connection, "AbsSystemBox.Sensors", image, "--service");
            session = new();
            pipe = connection;
            return true;
        }
        catch { connection.Dispose(); throw; }
    }

    private async Task StartAsync(bool elevate, CancellationToken token)
    {
        Stop();
        var executable = Path.Combine(AppContext.BaseDirectory, "sensors", "SysBox.SensorHost.exe");
        if (!File.Exists(executable)) throw new IOException("独立硬件采集组件缺失，请使用完整构建。");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var nonce = Guid.NewGuid().ToString("N");
        session = new();
        var connection = NamedPipeServerStreamAcl.Create("AbsSystemBox.Sensors." + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, acl);
        pipe = connection;
        var start = new ProcessStartInfo(executable, nonce) { UseShellExecute = elevate, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = !elevate };
        if (elevate) start.Verb = "runas";
        process = Process.Start(start) ?? throw new IOException("无法启动硬件采集组件。");
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var connected = connection.WaitForConnectionAsync(combined.Token);
        if (await Task.WhenAny(connected, process.WaitForExitAsync(combined.Token)) != connected)
            throw new IOException("硬件采集组件已退出。");
        await connected;
        if (PeerIdentity.ProcessId(connection, false) != (uint)process.Id || !string.Equals(PeerIdentity.Image(connection, false), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("硬件采集组件身份不匹配。");
    }

    private async Task<CoolingSensorSnapshot> ReadConnectedAsync(CancellationToken token)
    {
        var connection = pipe ?? throw new IOException("硬件采集已断开。");
        await Protocol.WriteAsync(connection, new RpcRequest(Protocol.Version, "cooling.sensors.read", JsonSerializer.SerializeToElement(new { }, Protocol.Json)), token);
        var response = await Protocol.ReadAsync<RpcResponse>(connection, token);
        if (response.Version != Protocol.Version || response.Error is not null || response.Value is null)
            throw new IOException(response.Error?.Message ?? "硬件采集返回无效数据。");
        var snapshot = response.Value.Value.Deserialize<CoolingSensorSnapshot>(Protocol.Json) ?? throw new IOException("硬件采集返回空数据。");
        if (snapshot.Sensors.Length > 256) throw new IOException("传感器数量超过上限。");
        return snapshot;
    }

    public void Stop()
    {
        var previous = Interlocked.Exchange(ref session, null);
        previous?.Cancel();
        Interlocked.Exchange(ref pipe, null)?.Dispose();
        Interlocked.Exchange(ref process, null)?.Dispose();
        previous?.Dispose();
    }
    public void Dispose() => Stop();
}
