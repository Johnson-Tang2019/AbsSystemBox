// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class DockWindowProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public int X; [FieldOffset(12)] public int Y; [FieldOffset(16)] public uint Data; [FieldOffset(20)] public uint Flags; [FieldOffset(24)] public uint Time; [FieldOffset(32)] public IntPtr Extra; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, RegionHandle region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(RegionHandle region, out Rect rect);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    private sealed class RegionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RegionHandle() : base(true) { SetHandle(CreateRectRgn(0, 0, 0, 0)); }
        protected override bool ReleaseHandle() => DeleteObject(handle);
    }
    public static void Move(int x, int y)
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width < 1 || height < 1 || x < left || y < top || x >= (long)left + width || y >= (long)top + height) throw new ArgumentOutOfRangeException();
        var input = new Input { Type = 0, X = (int)((x - (long)left + 0.5) * 65536 / width), Y = (int)((y - (long)top + 0.5) * 65536 / height), Flags = 0xC001 };
        if (Marshal.SizeOf<Input>() != 40 || SendInput(1, new[] { input }, 40) != 1) throw new Win32Exception();
    }
    public static void Button(uint flags)
    {
        if (Marshal.SizeOf<Input>() != 40 || SendInput(1, new[] { new Input { Type = 0, Flags = flags } }, 40) != 1) throw new Win32Exception();
    }
    public static Rect Bounds(IntPtr window) { if (!GetWindowRect(window, out var rect)) throw new Win32Exception(); return rect; }
    public static Rect Region(IntPtr window)
    {
        using (var region = new RegionHandle())
        {
            if (region.IsInvalid || GetWindowRgn(window, region) == 0 || GetRgnBox(region, out var rect) == 0) throw new Win32Exception();
            return rect;
        }
    }
    public static Rect WorkArea(IntPtr window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (info.Size != 40 || !GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) throw new Win32Exception();
        return info.Work;
    }
}
