// SPDX-License-Identifier: GPL-3.0-or-later
// Validation-only: remove this process's privileges and disable its administrator SID.
// No account, UAC setting, service or application permission is modified.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class RestrictedUserLauncher
{
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disabledCount, ref SidAttributes disabled,
        uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restricted, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int kind, ref SidAttributes data, uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string app, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);

    public static Process Launch(string executable, string arguments)
    {
        IntPtr source = IntPtr.Zero, restricted = IntPtr.Zero, admin = IntPtr.Zero, medium = IntPtr.Zero;
        try
        {
            using (var current = Process.GetCurrentProcess())
                if (!OpenProcessToken(current.Handle, 0x008B, out source)) throw new Win32Exception();
            if (!ConvertStringSidToSid("S-1-5-32-544", out admin)) throw new Win32Exception();
            var disabled = new SidAttributes { Sid = admin };
            // DISABLE_MAX_PRIVILEGE | LUA_TOKEN; do not use SANDBOX_INERT or alter enforcement policy.
            if (!CreateRestrictedToken(source, 5, 1, ref disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted)) throw new Win32Exception();
            if (!ConvertStringSidToSid("S-1-16-8192", out medium)) throw new Win32Exception();
            var label = new SidAttributes { Sid = medium, Attributes = 0x20 };
            if (!SetTokenInformation(restricted, 25, ref label, (uint)Marshal.SizeOf(typeof(SidAttributes)) + GetLengthSid(medium))) throw new Win32Exception();
            var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = "winsta0\\default", Flags = 1, ShowWindow = 1 };
            ProcessInfo info;
            if (!CreateProcessAsUserW(restricted, executable, new StringBuilder("\"" + executable + "\" " + arguments),
                IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out info)) throw new Win32Exception();
            try
            {
                var process = Process.GetProcessById(info.ProcessId);
                // Retain a handle before the original one closes so even fast exits have a readable exit code.
                var retainedHandle = process.Handle;
                return process;
            }
            finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
        }
        finally
        {
            if (medium != IntPtr.Zero) LocalFree(medium);
            if (admin != IntPtr.Zero) LocalFree(admin);
            if (restricted != IntPtr.Zero) CloseHandle(restricted);
            if (source != IntPtr.Zero) CloseHandle(source);
        }
    }
}
