// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Transport;

public sealed record RpcRequest(string Version, string Method, JsonElement Arguments);
public sealed record RpcResponse(string Version, JsonElement? Value, SysBoxError? Error);
public sealed record WireValue<T>(T? Value, SysBoxError? Error);
public sealed record Observation(string Status, string Notification, RuleSample[] Samples);
public sealed record JobStatus(string Id, bool Finished, IndexProgress? Progress, SysBoxError? Error);
public static class Protocol
{
    public const string Version = "1.0";
    public const int MaxFrame = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var name = "AbsSystemBox.Core." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.User!.Value)))[..24] + "." + Process.GetCurrentProcess().SessionId;
#if DEBUG
            var suffix = Environment.GetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX");
            if (suffix is { Length: > 0 and <= 40 } && suffix.All(char.IsAsciiLetterOrDigit))
                name += "." + suffix;
#endif
            return name;
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxFrame)
            throw new SysBoxOperationException(new("RESPONSE_LIMIT", "数据超出通信上限，请减少请求范围。"));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxFrame)
            throw new IOException("非法通信帧长度。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new IOException("非法协议对象。");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new IOException("空协议对象。");
    }
    public static string Companion(string folder, string file)
    {
        var basePath = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Path.GetFileName(basePath) is "host" or "cli" or "mcp")
            basePath = Path.GetDirectoryName(basePath)!;
        var packaged = folder == "" ? Path.Combine(basePath, file) : Path.Combine(basePath, folder, file);
        if (File.Exists(packaged))
            return packaged;
#if DEBUG
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            if (!File.Exists(Path.Combine(parent.FullName, "SysBox.slnx")))
                continue;
            var project = folder switch
            {
                "host" => "SysBox.CoreHost",
                "cli" => "SysBox.Cli",
                "mcp" => "SysBox.Mcp",
                _ => "SysBox.App"
            };
            return project == "SysBox.App" ? Path.Combine(parent.FullName, "src", project, "bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64", file) : Path.Combine(parent.FullName, "src", project, "bin/Debug/net10.0-windows", file);
        }
#endif
        return packaged;
    }
}

public static class PeerIdentity
{
    public static uint ProcessId(PipeStream pipe, bool server)
    {
        uint pid;
        if (!(server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid)))
            throw new UnauthorizedAccessException("无法确认对端进程。");
        return pid;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle pipe, StringBuilder name, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    public static string Image(PipeStream pipe, bool server)
    {
        if (!server)
            RequireLocalClient(pipe);
        uint pid;
        var success = server ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid) : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
        if (!success)
            throw new UnauthorizedAccessException("无法确认管道对端进程。");
        using var process = Process.GetProcessById(checked((int)pid));
        using var handle = OpenProcess(0x1000, false, pid);
        if (handle.IsInvalid || process.SessionId != Process.GetCurrentProcess().SessionId || !OpenProcessToken(handle, 8, out var token))
            throw new UnauthorizedAccessException("对端会话不可用。");
        using var current = WindowsIdentity.GetCurrent();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User is null || current.User is null || !identity.User.Equals(current.User))
                throw new UnauthorizedAccessException("对端用户身份不匹配。");
        return ReadImage(handle);
    }
    public static void VerifyLaunchedClient(PipeStream pipe, Process process, string expectedImage)
    {
        RequireLocalClient(pipe);
        // Bind the pipe PID to the process handle returned by the elevation launch.
        if (ProcessId(pipe, false) != (uint)process.Id ||
            !string.Equals(ReadImage(process.SafeHandle), Path.GetFullPath(expectedImage), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("管理员组件身份不匹配。");
    }
    private static void RequireLocalClient(PipeStream pipe)
    {
        var computer = new StringBuilder(256);
        // Local pipes report ERROR_PIPE_LOCAL; success identifies a network connection.
        var remote = GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, computer, 256);
        if (remote || Marshal.GetLastWin32Error() != 229)
            throw new UnauthorizedAccessException("不接受远程或无法确认来源的管道请求。");
    }
    private static string ReadImage(SafeProcessHandle handle)
    {
        var image = new StringBuilder(32768);
        uint length = 32768;
        if (!QueryFullProcessImageNameW(handle, 0, image, ref length))
            throw new UnauthorizedAccessException("无法确认对端映像。");
        return Path.GetFullPath(image.ToString());
    }
    public static bool IsUi(string image) => string.Equals(image, Path.GetFullPath(Protocol.Companion("", "SysBox.App.exe")), StringComparison.OrdinalIgnoreCase);
}

public sealed class RpcClient
{
    private static readonly SemaphoreSlim startGate = new(1, 1);
    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            try
            {
                await pipe.ConnectAsync(250, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await startGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var executable = Protocol.Companion("host", "SysBox.CoreHost.exe");
                    if (!File.Exists(executable))
                        throw new IOException("后台组件缺失，请重新安装完整程序。");
                    BackgroundProcess.Start(executable);
                    await pipe.ConnectAsync(5000, token).ConfigureAwait(false);
                }
                finally { startGate.Release(); }
            }
            if (!string.Equals(PeerIdentity.Image(pipe, true), Path.GetFullPath(Protocol.Companion("host", "SysBox.CoreHost.exe")), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("后台进程映像不匹配。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task<T> CallAsync<T>(string method, object arguments, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var pipe = await ConnectAsync(timeout.Token).ConfigureAwait(false);
            await Protocol.WriteAsync(pipe, new RpcRequest(Protocol.Version, method, JsonSerializer.SerializeToElement(arguments, Protocol.Json)), timeout.Token).ConfigureAwait(false);
            var response = await Protocol.ReadAsync<RpcResponse>(pipe, timeout.Token).ConfigureAwait(false);
            if (response.Version != Protocol.Version)
                throw new IOException("后台协议版本不兼容。");
            if (response.Error is { } error)
                throw new SysBoxOperationException(error);
            if (response.Value is not { } value)
                throw new IOException("后台响应缺少数据。");
            return value.Deserialize<T>(Protocol.Json) ?? throw new IOException("后台响应为空。");
        }
        catch (SysBoxOperationException) { throw; }
        catch (TimeoutException) { throw new SysBoxOperationException(new("CORE_TIMEOUT", "后台连接超时。", retryable: true)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SysBoxOperationException(new("CORE_TIMEOUT", "后台响应超时。", retryable: true)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception)
        {
#if DEBUG
            var directory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "rpc-error.txt"), e.ToString());
            }
#endif
            throw new SysBoxOperationException(new("CORE_DISCONNECTED", "无法连接可信后台，请重新打开应用。", retryable: true));
        }
    }
    public T Call<T>(string method, object arguments)
        => Task.Run(() => CallAsync<T>(method, arguments)).GetAwaiter().GetResult();
}
