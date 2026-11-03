// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;
namespace SysBox.Native;

public static class WorkingSetAccess
{
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyWorkingSet(SafeProcessHandle process);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out uint value, int length, out int required);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    public static CleanupAttempt Trim(uint pid, DateTimeOffset expectedStart, string expectedName, ulong minimumBytes)
    {
        var now = DateTimeOffset.UtcNow;
        CleanupAttempt Skip(string reason, ulong? before = null) => new(pid, expectedName, now, before, null, false, reason);
        if (!OperatingSystem.IsWindows() || pid is 0 or 4 || pid == Environment.ProcessId)
            return Skip("系统或自身进程已保护。");
        var forbidden = new[] { "system", "registry", "memory compression", "idle", "fontdrvhost.exe", "audiodg.exe", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe", "dwm.exe", "explorer.exe", "msmpeng.exe", "code.exe", "codex.exe", "java.exe", "javaw.exe", "clash-verge.exe" };
        if (expectedName.StartsWith("sysbox", StringComparison.OrdinalIgnoreCase) || expectedName.StartsWith("absSystemBox", StringComparison.OrdinalIgnoreCase) || forbidden.Contains(expectedName, StringComparer.OrdinalIgnoreCase))
            return Skip("受保护进程已排除。");
        if (now - expectedStart < TimeSpan.FromSeconds(120))
            return Skip("进程启动未满 120 秒。");
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (foreground == pid)
            return Skip("前台进程已排除。");
        using var process = MemoryInterop.OpenProcess(0x1100, false, pid);
        if (process.IsInvalid)
            return Skip("没有工作集调整权限或进程已退出。");
        if (!MemoryInterop.GetProcessTimes(process, out var created, out _, out _, out _) || new DateTimeOffset(DateTime.FromFileTimeUtc(created)) != expectedStart)
            return Skip("进程身份已改变。");
        var image = new char[32768];
        var length = (uint)image.Length;
        if (!MemoryInterop.QueryFullProcessImageName(process, 0, image, ref length) || length > image.Length || !string.Equals(Path.GetFileName(new string(image, 0, (int)length)), expectedName, StringComparison.OrdinalIgnoreCase))
            return Skip("进程映像不匹配。");
        if (!OpenProcessToken(process, 8, out var token))
            return Skip("无法确认进程用户。");
        using (token)
        using (var target = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (target.User is null || current.User is null || !target.User.Equals(current.User) || target.IsSystem)
                return Skip("不是当前用户的普通进程。");
            if (!GetTokenInformation(token, 20, out var elevated, 4, out var elevationBytes) || elevationBytes != 4 || elevated != 0)
                return Skip("提升权限的进程已保护。");
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (!GetTokenInformation(token, 12, out var session, 4, out var sessionBytes) || sessionBytes != 4 || session != (uint)self.SessionId)
                return Skip("其他登录会话的进程已保护。");
        }
        var counter = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        if (counter.Size != 80 || !MemoryInterop.GetProcessMemoryInfo(process, ref counter, counter.Size))
            return Skip("无法读取工作集。");
        var before = (ulong)counter.WorkingSetSize;
        if (before < minimumBytes)
            return Skip("工作集低于最小处理值。", before);
        GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
        if (foreground == pid)
            return Skip("已切换为前台进程。", before);
        if (!EmptyWorkingSet(process))
            return Skip("系统拒绝调整工作集。", before);
        ulong? after = MemoryInterop.GetProcessMemoryInfo(process, ref counter, counter.Size) ? (ulong)counter.WorkingSetSize : null;
        return new(pid, expectedName, now, before, after, true, "已调整工作集；不等同于物理内存释放，系统可能重新调入页面。");
    }
}
