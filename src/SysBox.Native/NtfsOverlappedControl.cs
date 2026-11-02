// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SysBox.Native;

internal static class NtfsOverlappedControl
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event;
    }
    private sealed class Allocation : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Allocation(int size) : base(true) => SetHandle(Marshal.AllocHGlobal(size));
        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputBytes, IntPtr output, uint outputBytes, out uint returned, Allocation overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, Allocation overlapped, out uint returned, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, Allocation overlapped);

    public static byte[] Read(SafeFileHandle file, uint code, byte[]? input, bool allowEnd, CancellationToken token, out bool ended)
    {
        token.ThrowIfCancellationRequested();
        ended = false;
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset);
        var size = Marshal.SizeOf<Overlapped>();
        if (size != (IntPtr.Size == 8 ? 32 : 20))
            throw new PlatformNotSupportedException();
        using var state = new Allocation(size);
        Marshal.StructureToPtr(new Overlapped { Event = signal.SafeWaitHandle.DangerousGetHandle() }, state.DangerousGetHandle(), false);
        using var nativeInput = new Allocation(Math.Max(1, input?.Length ?? 0));
        using var output = new Allocation(65536);
        if (input is not null)
            Marshal.Copy(input, 0, nativeInput.DangerousGetHandle(), input.Length);
        var success = DeviceIoControl(file, code, nativeInput.DangerousGetHandle(), (uint)(input?.Length ?? 0), output.DangerousGetHandle(), 65536, out var returned, state);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        if (!success && error == 997)
        {
            var ready = WaitHandle.WaitAny([signal, token.WaitHandle], TimeSpan.FromSeconds(15));
            if (ready != 0)
            {
                // Do not release native buffers until cancellation has completed in the driver.
                CancelIoEx(file, state);
                GetOverlappedResult(file, state, out _, true);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("NTFS 只读 I/O 超时。");
            }
            success = GetOverlappedResult(file, state, out returned, false);
            error = success ? 0 : Marshal.GetLastWin32Error();
        }
        if (!success)
        {
            if (allowEnd && error == 38)
            {
                ended = true;
                return [];
            }
            throw new Win32Exception(error);
        }
        token.ThrowIfCancellationRequested();
        if (returned > 65536)
            throw new IOException("Native buffer length exceeds allocation.");
        var result = new byte[checked((int)returned)];
        Marshal.Copy(output.DangerousGetHandle(), result, 0, result.Length);
        return result;
    }
}
