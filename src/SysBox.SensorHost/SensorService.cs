// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysBox.Abstractions;
using SysBox.NtfsBroker;
using SysBox.Transport;

namespace SysBox.SensorHost;

public sealed record SensorServiceGrant(string Sid, string Image);

public sealed record SensorServicePolicy(int Version, SensorServiceGrant[] Grants)
{
    public const string ServiceName = "AbsSystemBox.Sensors";
    public const string PipeName = "AbsSystemBox.Sensors.Service.v1";
    public static string ServiceDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "SensorService");
    public static string ServiceImage => Path.Combine(ServiceDirectory, "SysBox.SensorHost.exe");
    public static string Image => ServiceImage;
    public static string PolicyDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AbsSystemBox", "SensorService");
    public static string PolicyPath => Path.Combine(PolicyDirectory, "grants.json");
    public static SensorServicePolicy Load()
    {
        var path = Path.Combine(PolicyDirectory, "grants.json");
        BrokerPolicy.VerifyProtected(path);
        if (new FileInfo(path).Length > 65536) throw new IOException("传感器授权配置过大。");
        var policy = JsonSerializer.Deserialize<SensorServicePolicy>(File.ReadAllText(path)) ?? throw new IOException("传感器授权配置损坏。");
        policy.Validate();
        return policy;
    }
    public void Validate()
    {
        if (Version != 1 || Grants is null || Grants.Length is < 1 or > 32)
            throw new IOException("传感器授权配置版本或数量无效。");
        foreach (var grant in Grants)
        {
            if (grant is null || grant.Sid is null || !grant.Sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
                throw new IOException("传感器授权用户无效。");
            _ = new SecurityIdentifier(grant.Sid);
            if (string.IsNullOrWhiteSpace(grant.Image) || grant.Image.Length > 32760 || !Path.IsPathFullyQualified(grant.Image) ||
                grant.Image.Length < 4 || grant.Image[1] != ':' || !char.IsAsciiLetter(grant.Image[0]) ||
                !string.Equals(Path.GetFileName(grant.Image), "SysBox.App.exe", StringComparison.OrdinalIgnoreCase) ||
                grant.Image.IndexOf(':', 2) >= 0)
                throw new IOException("传感器授权目标无效。");
        }
    }
    public bool Allows(BrokerPeer peer) => peer.Session > 0 && Grants.Any(grant => grant.Sid == peer.Sid &&
        string.Equals(Path.GetFullPath(grant.Image), Path.GetFullPath(peer.Image), StringComparison.OrdinalIgnoreCase));
    public void ValidateRequest(BrokerPeer peer, RpcRequest request)
    {
        if (!Allows(peer)) throw new UnauthorizedAccessException("未授权此界面读取传感器。");
        if (request.Version != Protocol.Version || request.Method != "cooling.sensors.read" ||
            request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.EnumerateObject().Any())
            throw new SysBoxOperationException(new("INVALID_SENSOR_REQUEST", "传感器服务仅接受空参数的只读采样请求。"));
    }
    public PipeSecurity Security()
    {
        var acl = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(system);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        foreach (var sid in Grants.Select(grant => grant.Sid).Distinct(StringComparer.Ordinal))
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return acl;
    }
    public static void VerifyPayload()
    {
        var manifestPath = Path.Combine(ServiceDirectory, "sensors-files.json");
        BrokerPolicy.VerifyProtected(manifestPath);
        if (new FileInfo(manifestPath).Length > 2 * 1024 * 1024) throw new IOException("传感器服务清单过大。");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.GetProperty("product").GetString() != "AbsSystemBox.JohnsonTang2019.Sensors") throw new IOException("传感器服务清单身份无效。");
        var files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > 2000) throw new IOException("传感器服务清单数量无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var item in files.EnumerateArray())
        {
            var relative = item.GetProperty("path").GetString() ?? throw new IOException("服务清单路径缺失。");
            if (relative.Length is < 1 or > 800 || Path.IsPathRooted(relative) || relative.Contains(':') ||
                relative.Split(['/', '\\']).Any(part => part is ".." or "." or "" || part != part.TrimEnd(' ', '.') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) || !names.Add(relative.Replace('/', '\\')))
                throw new IOException("服务清单路径无效。");
            var path = Path.GetFullPath(Path.Combine(ServiceDirectory, relative));
            if (!path.StartsWith(ServiceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("服务清单路径越界。");
            BrokerPolicy.VerifyProtected(path);
            var length = new FileInfo(path).Length;
            totalBytes = checked(totalBytes + length);
            if (length > 64 * 1024 * 1024 || totalBytes > 512 * 1024 * 1024) throw new IOException("传感器服务负载超出大小上限。");
            var expected = item.GetProperty("sha256").GetString();
            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传感器服务负载摘要不匹配。");
        }
        if (!names.Contains("SysBox.SensorHost.exe") || !names.Contains("SysBox.SensorHost.dll") || !names.Contains("LibreHardwareMonitorLib.dll"))
            throw new IOException("传感器服务负载不完整。");
    }
}

public static class SensorService
{
    public static async Task RunAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != "S-1-5-18" || !string.Equals(Environment.ProcessPath, SensorServicePolicy.ServiceImage, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("传感器服务仅允许从受保护安装位置以系统身份启动。");
        BrokerPolicy.VerifyProtected(SensorServicePolicy.ServiceImage);
        SensorServicePolicy.VerifyPayload();
        var policy = SensorServicePolicy.Load();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(options => options.ServiceName = SensorServicePolicy.ServiceName);
        builder.Logging.ClearProviders();
        builder.Logging.AddEventLog(options => options.SourceName = SensorServicePolicy.ServiceName);
        builder.Services.AddSingleton(policy);
        builder.Services.AddHostedService<SensorServiceWorker>();
        await builder.Build().RunAsync();
    }
}

sealed class SensorServiceWorker(SensorServicePolicy policy) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(SensorServicePolicy.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 16384, 16384, policy.Security(), HandleInheritability.None);
            await pipe.WaitForConnectionAsync(token);
            await HandleAsync(pipe, token);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        HardwareSensorSession? hardware = null;
        using var watchdog = new NativeBudget();
        try
        {
            while (!stopping.IsCancellationRequested && pipe.IsConnected)
            {
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                requestDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                var request = await Protocol.ReadAsync<RpcRequest>(pipe, requestDeadline.Token);
                var peer = BrokerPeer.Read(pipe, false);
                policy.ValidateRequest(peer, request);
                watchdog.Arm(TimeSpan.FromSeconds(15));
                // A protected LocalSystem service and its protected SID/image grant authorize this fixed operation.
                // No request field or caller-controlled Boolean can enable privileged sensors.
                hardware ??= HardwareSensorSession.Create(true);
                var snapshot = hardware.Read();
                watchdog.Disarm();
                await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(snapshot, Protocol.Json), null), requestDeadline.Token);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            watchdog.Disarm();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var code = error is UnauthorizedAccessException ? "ACCESS_DENIED" : error is SysBoxOperationException operation ? operation.Error.Code : "SENSOR_UNAVAILABLE";
                await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, null, new(code, "传感器采样已停止，请检查授权或重新连接。")), deadline.Token);
            }
            catch (Exception responseError) when (responseError is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally
        {
            watchdog.Arm(TimeSpan.FromSeconds(2));
            hardware?.Dispose();
            watchdog.Disarm();
        }
    }
}

sealed class NativeBudget : IDisposable
{
    private long deadline = long.MaxValue;
    private readonly Timer timer;
    public NativeBudget() => timer = new Timer(_ =>
    {
        if (Stopwatch.GetTimestamp() >= Interlocked.Read(ref deadline)) Environment.Exit(1);
    }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    public void Arm(TimeSpan duration) => Interlocked.Exchange(ref deadline, checked(Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency)));
    public void Disarm() => Interlocked.Exchange(ref deadline, long.MaxValue);
    public void Dispose() => timer.Dispose();
}
