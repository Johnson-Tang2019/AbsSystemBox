// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;
using SysBox.Transport;
using SysBox.SensorHost;

try
{
    if (args is ["--service"]) await SensorService.RunAsync();
    else await RunAsync(args);
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    // A failed handshake, timeout or parent exit never authorizes a fallback operation.
}

static async Task RunAsync(string[] arguments)
{
    if (arguments.Length != 1 || !Guid.TryParseExact(arguments[0], "N", out _))
        return;

    using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(12));
    var watchdog = new WorkerWatchdog(lifetime.Token);
    watchdog.Arm(TimeSpan.FromSeconds(15));
    watchdog.Start();
    await using var pipe = new NamedPipeClientStream(".", "AbsSystemBox.Sensors." + arguments[0],
        PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(15000, lifetime.Token);
    watchdog.Attach(pipe.SafePipeHandle);
    var expectedImage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SysBox.App.exe"));
    if (!string.Equals(PeerIdentity.Image(pipe, true), expectedImage, StringComparison.OrdinalIgnoreCase))
        return;

    using var parent = Process.GetProcessById(checked((int)PeerIdentity.ProcessId(pipe, true)));
    var parentWatch = WatchParentAsync(parent, lifetime);
    HardwareSensorSession? computer = null;
    string message = "独立采集显卡温度与转速；CPU 和主板传感器需要单独授权。";
    try
    {
        while (!lifetime.IsCancellationRequested && !parent.HasExited)
        {
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            watchdog.Arm(TimeSpan.FromMinutes(5));
            requestDeadline.CancelAfter(TimeSpan.FromMinutes(5));
            var request = await Protocol.ReadAsync<RpcRequest>(pipe, requestDeadline.Token);
            if (request.Version != Protocol.Version || request.Method != "cooling.sensors.read" ||
                request.Arguments.ValueKind != JsonValueKind.Object || request.Arguments.EnumerateObject().Any())
                return;

            if (computer is null)
            {
                using var identity = WindowsIdentity.GetCurrent();
                bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                bool privilegedSensors = false;
                if (administrator)
                {
                    watchdog.Arm(TimeSpan.FromMinutes(2));
                    privilegedSensors = Native.MessageBoxW(IntPtr.Zero,
                        "是否允许本次独立采集 CPU 温度和主板风扇读数？\n\n使用已安装的 PawnIO 驱动和固定的监测模块，不设置风扇、泵速或超频参数。停止同步或退出后结束采集。",
                        "AbsSystemBox 硬件采集授权", 0x40134) == 6;
                    watchdog.Arm(TimeSpan.FromSeconds(15));
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (parent.HasExited) return;
                    if (privilegedSensors)
                    {
                        using var driver = Native.CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 3, 3,
                            IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                        privilegedSensors = !driver.IsInvalid;
                        message = privilegedSensors
                            ? "已授权独立采集 CPU、主板和显卡；仅显示硬件实际提供的温度与转速。"
                            : "PawnIO 驱动不可访问，CPU 和主板采集不可用；仅采集显卡读数。";
                    }
                    else
                        message = "已取消 CPU 和主板采集授权；仅采集显卡读数。";
                }

                watchdog.Arm(TimeSpan.FromSeconds(15));
                computer = HardwareSensorSession.Create(privilegedSensors);
            }

            lifetime.Token.ThrowIfCancellationRequested();
            watchdog.Arm(TimeSpan.FromSeconds(15));
            var snapshot = computer.Read(lifetime.Token) with { Message = message };
            await Protocol.WriteAsync(pipe,
                new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(snapshot, Protocol.Json), null),
                lifetime.Token);
            watchdog.Arm(TimeSpan.FromMinutes(5));
        }
    }
    finally
    {
        // No IControl setters are called. LHM performs its normal monitoring-resource cleanup.
        watchdog.Arm(TimeSpan.FromSeconds(2));
        computer?.Dispose();
        lifetime.Cancel();
        await parentWatch;
    }
}

static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
{
    try
    {
        await parent.WaitForExitAsync(lifetime.Token);
        lifetime.Cancel();
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
}

sealed class WorkerWatchdog(CancellationToken lifetime)
{
    private long deadline;
    private volatile SafePipeHandle? pipe;
    public void Attach(SafePipeHandle handle) => pipe = handle;
    public void Arm(TimeSpan duration) => Interlocked.Exchange(ref deadline,
        checked(Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency)));
    public void Start() => _ = Task.Run(async () =>
    {
        // Cancellation does not interrupt synchronous hardware APIs. This process owns only sampling.
        while (true)
        {
            if (lifetime.IsCancellationRequested || Stopwatch.GetTimestamp() >= Interlocked.Read(ref deadline))
                Environment.Exit(0);
            var handle = pipe;
            if (handle is not null && (handle.IsClosed || handle.IsInvalid ||
                !Native.PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero)))
                Environment.Exit(0);
            await Task.Delay(250);
        }
    });
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size,
        IntPtr bytesRead, out uint bytesAvailable, IntPtr bytesLeft);
}
