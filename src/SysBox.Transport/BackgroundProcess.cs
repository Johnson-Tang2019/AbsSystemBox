// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysBox.Transport;

internal static class BackgroundProcess
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public uint Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreatedProcess
    {
        public IntPtr Process, Thread; public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref Startup startup, out CreatedProcess process);
    public static void Start(string executable)
    {
        // A GUI background host must not inherit CLI/MCP stdout or delay their EOF.
        if (IntPtr.Size != 8 || Marshal.SizeOf<Startup>() != 104 || Marshal.SizeOf<CreatedProcess>() != 24)
            throw new PlatformNotSupportedException("Background launch requires verified Windows x64 native layouts.");
        var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>() };
        if (!CreateProcessW(executable, new StringBuilder('"' + executable + '"'), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out var created))
            throw new IOException("无法启动后台组件。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        using var process = new SafeProcessHandle(created.Process, true);
        using var thread = new SafeWaitHandle(created.Thread, true);
    }
}
