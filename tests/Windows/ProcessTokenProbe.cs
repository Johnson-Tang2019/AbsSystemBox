// SPDX-License-Identifier: GPL-3.0-or-later
// Test-only read of our launched application's token; never linked into the product.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class ProcessTokenProbe
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, out int data, int size, out int needed);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateToken(IntPtr token, int level, out IntPtr duplicate);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CheckTokenMembership(IntPtr token, IntPtr sid, out bool member);

    public static bool IsElevated(Process process)
    {
        IntPtr token;
        if (!OpenProcessToken(process.Handle, 8, out token)) throw new Win32Exception();
        try
        {
            int value, needed;
            if (!GetTokenInformation(token, 20, out value, 4, out needed)) throw new Win32Exception();
            return value != 0;
        }
        finally { CloseHandle(token); }
    }

    public static bool IsAdministrator(Process process)
    {
        IntPtr token = IntPtr.Zero, copy = IntPtr.Zero, sid = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process.Handle, 10, out token)) throw new Win32Exception();
            if (!DuplicateToken(token, 2, out copy)) throw new Win32Exception();
            if (!ConvertStringSidToSid("S-1-5-32-544", out sid)) throw new Win32Exception();
            bool member;
            if (!CheckTokenMembership(copy, sid, out member)) throw new Win32Exception();
            return member;
        }
        finally
        {
            if (sid != IntPtr.Zero) LocalFree(sid);
            if (copy != IntPtr.Zero) CloseHandle(copy);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
