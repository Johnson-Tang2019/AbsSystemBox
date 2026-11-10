// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SysBox.Abstractions;
using SysBox.Native;
using SysBox.Memory;
using SysBox.Transport;
using System.Text.Json;
try { Run(args); } catch (Exception error) when (error is not OutOfMemoryException) {/* A failed handshake or cancelled parent must not execute a fallback action. */}
static void Run(string[] args)
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || args.Length != 1 || !Guid.TryParseExact(args[0], "N", out _))
        return;
    using var activity = new Mutex(false, "Local\\AbsSystemBox.Cleanup");
    bool entered;
    try
    {
        entered = activity.WaitOne(0);
    }
    catch (AbandonedMutexException) { entered = true; }
    if (!entered)
        return;
    try
    {
        // The thread that acquires the Windows mutex must also release it.
        RunRequestAsync(args[0]).GetAwaiter().GetResult();
    }
    finally { activity.ReleaseMutex(); }
}
static async Task RunRequestAsync(string nonce)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await using var pipe = new NamedPipeClientStream(".", "AbsSystemBox.Cleanup." + nonce, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(15000, deadline.Token);
    var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SysBox.App.exe"));
    if (!string.Equals(PeerIdentity.Image(pipe, true), expected, StringComparison.OrdinalIgnoreCase))
        return;
    using var parent = System.Diagnostics.Process.GetProcessById(checked((int)PeerIdentity.ProcessId(pipe, true)));
    _ = parent.WaitForExitAsync(deadline.Token).ContinueWith(task =>
    {
        if (task.IsCompletedSuccessfully)
            try { deadline.Cancel(); } catch (ObjectDisposedException) { }
    }, TaskScheduler.Default);
    var request = await Protocol.ReadAsync<RpcRequest>(pipe, deadline.Token);
    if (request.Version != Protocol.Version || request.Method != "cleanup.system")
        return;
    var areas = MemoryCleanupCatalog.Validate(request.Arguments.Deserialize<MemoryCleanupArea[]>(Protocol.Json) ?? []);
    // A second request is invalid; a completed read also detects the UI closing or timing out.
    var disconnected = pipe.ReadAsync(new byte[1], deadline.Token).AsTask();
    var text = "将执行以下系统内存操作：\n" + string.Join("\n", areas.Select(MemoryCleanupCatalog.Name)) + "\n\n可能造成短暂停顿或重新调页。全局工作集操作不具备逐进程的前台保护。是否执行本次操作？";
    MemoryCleanupReport report;
    try
    {
        if (Consent.MessageBoxW(IntPtr.Zero, text, "AbsSystemBox 管理员清理确认", 0x50134) != 6)
            report = new(true, [], null);
        else
        {
            var monitor = new WindowsMemoryMonitor(new WindowsMemoryApi());
            var before = await monitor.ReadAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (disconnected.IsCompleted || parent.HasExited)
                return;
            var results = SystemMemoryCleanup.Execute(areas, deadline.Token);
            await Task.Delay(750, deadline.Token);
            var after = await monitor.ReadAsync(deadline.Token);
            report = new(false, results, before.IsSuccess && after.IsSuccess ? checked((long)after.Value.AvailablePhysicalBytes - (long)before.Value.AvailablePhysicalBytes) : null);
        }
        await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(report, Protocol.Json), null), deadline.Token);
    }
    finally
    {
        deadline.Cancel();
        try { await disconnected; }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }
}
static class Consent
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(IntPtr parent, string text, string caption, uint kind);
}
