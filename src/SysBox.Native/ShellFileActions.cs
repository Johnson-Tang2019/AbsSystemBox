// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysBox.Native;

public static class ShellFileActions
{
    private sealed class SafePidl : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePidl() : base(true) { }
        protected override bool ReleaseHandle() { ILFree(handle); return true; }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafePidl ILCreateFromPathW(string path);
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(SafePidl pidl, uint count, IntPtr children, uint flags);

    // Called only from an explicit desktop UI gesture. No command-line construction.
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException();
        using var pidl = ILCreateFromPathW(path);
        if (pidl.IsInvalid) throw new Win32Exception("无法解析文件位置。");
        Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
    }
}
