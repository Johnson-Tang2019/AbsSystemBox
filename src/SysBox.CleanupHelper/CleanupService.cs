// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SysBox.Abstractions;
using SysBox.Memory;
using SysBox.Native;
using SysBox.NtfsBroker;
using SysBox.Transport;

internal sealed record CleanupGrant(string Sid, string Image, MemoryCleanupArea[] Areas)
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "CleanupService");
    public static string ImagePath => Path.Combine(DirectoryPath, "SysBox.CleanupHelper.exe");
    public const string Service = "AbsSystemBox.CleanupService";
    public const string Pipe = "AbsSystemBox.Cleanup.Persistent.v1";

    public static CleanupGrant Load()
    {
        var path = Path.Combine(DirectoryPath, "grant.json");
        BrokerPolicy.VerifyProtected(path);
        if (new FileInfo(path).Length > 8192) throw new IOException("清理授权文件过大。");
        var grant = JsonSerializer.Deserialize<CleanupGrant>(File.ReadAllText(path)) ?? throw new IOException("清理授权无效。");
        _ = new SecurityIdentifier(grant.Sid);
        if (!Path.IsPathFullyQualified(grant.Image) || Path.GetFileName(grant.Image) != "SysBox.App.exe")
            throw new IOException("清理授权的应用路径无效。");
        var areas = MemoryCleanupCatalog.Validate(grant.Areas ?? []);
        if (areas.Contains(MemoryCleanupArea.ProcessWorkingSets))
            throw new IOException("后台服务不支持逐进程工作集。");
        return grant with { Areas = areas };
    }

    public PipeSecurity Security()
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(new SecurityIdentifier(Sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return acl;
    }
}

internal sealed class CleanupService(CleanupGrant grant) : BackgroundService
{
    private long lastExecution;

    public static async Task RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18" || !string.Equals(Environment.ProcessPath, CleanupGrant.ImagePath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException();
        BrokerPolicy.VerifyProtected(CleanupGrant.ImagePath);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = CleanupGrant.DirectoryPath });
        builder.Services.AddWindowsService(options => options.ServiceName = CleanupGrant.Service);
        builder.Services.AddSingleton(CleanupGrant.Load());
        builder.Services.AddHostedService<CleanupService>();
        await builder.Build().RunAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(CleanupGrant.Pipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 16384, 16384, grant.Security());
            await pipe.WaitForConnectionAsync(token);
            await HandleAsync(pipe, token);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var request = await Protocol.ReadAsync<RpcRequest>(pipe, deadline.Token);
            var peer = BrokerPeer.Read(pipe, false);
            if (peer.Session == 0 || peer.Sid != grant.Sid || !string.Equals(peer.Image, grant.Image, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException();
            if (request.Version != Protocol.Version || request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.EnumerateObject().Any())
                throw new InvalidDataException();
            if (request.Method == "cleanup.grant.status")
            {
                await RespondAsync(grant.Areas);
                return;
            }
            if (request.Method != "cleanup.grant.execute") throw new InvalidDataException();
            var now = Environment.TickCount64;
            if (lastExecution != 0 && now - lastExecution < 10000) throw new InvalidOperationException("请等待十秒后再清理。");
            deadline.Token.ThrowIfCancellationRequested();
            lastExecution = now;
            using var watchdog = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            var monitor = new WindowsMemoryMonitor(new WindowsMemoryApi());
            var before = await monitor.ReadAsync(deadline.Token);
            if (!pipe.IsConnected) return;
            var results = SystemMemoryCleanup.Execute(grant.Areas, deadline.Token);
            var after = await monitor.ReadAsync(deadline.Token);
            await RespondAsync(new MemoryCleanupReport(false, results, before.IsSuccess && after.IsSuccess
                ? checked((long)after.Value.AvailablePhysicalBytes - (long)before.Value.AvailablePhysicalBytes) : null));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, null,
                    new("CLEANUP_FAILED", error is InvalidOperationException ? error.Message : "清理服务拒绝请求或暂不可用。")), reply.Token);
            }
            catch (Exception failure) when (failure is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        Task RespondAsync<T>(T value) => Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(value, Protocol.Json), null), deadline.Token);
    }
}
