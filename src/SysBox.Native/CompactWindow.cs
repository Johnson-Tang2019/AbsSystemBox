// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SysBox.Native;

public sealed class CompactWindow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        public Point Reserved, Maximum, MaximumPosition, MinimumTrack, MaximumTrack;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left, Top, Right, Bottom;
    }
    private delegate IntPtr Subclass(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, Subclass callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, Subclass callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, uint size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x, int y, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(Region region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    private readonly IntPtr window; private readonly Subclass callback;
    private (int X, int Y, int Width, int Height)? bounds;
    private (int X, int Y, int Width, int Height, int Diameter)? clip;
    private Region? hitRegion;
    public event Action? DisplayChanged;
    private sealed class Region : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Region(IntPtr value) : base(true) => SetHandle(value); protected override bool ReleaseHandle() => DeleteObject(handle);
    }
    public CompactWindow(IntPtr window)
    {
        this.window = window;
        callback = Handle;
        if (Marshal.SizeOf<Limits>() != 40 || Marshal.SizeOf<Rectangle>() != 16 || !SetWindowSubclass(window, callback, (UIntPtr)77, UIntPtr.Zero))
            throw new InvalidOperationException("无法配置悬浮窗尺寸。");
        var style = GetStyle(window, -20).ToInt64();
        SetStyle(window, -20, (IntPtr)((style | 0x80) & ~0x40000L));
        var normal = GetStyle(window, -16).ToInt64();
        SetStyle(window, -16, (IntPtr)((normal & ~0x00cf0000L) | 0x80000000L));
        uint border = 0xfffffffe;
        DwmSetWindowAttribute(window, 34, ref border, 4);
        SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37);
    }
    private IntPtr Handle(IntPtr w, uint message, UIntPtr p, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (message == 0x84)
        {
            var point = l.ToInt64();
            if (hitRegion is { } region && GetWindowRect(w, out var rectangle) &&
                !PtInRegion(region, unchecked((short)point) - rectangle.Left, unchecked((short)(point >> 16)) - rectangle.Top))
                return (IntPtr)(-1); // HTTRANSPARENT
            return (IntPtr)1; // HTCLIENT
        }
        if (message == 0x21)
            return (IntPtr)3; // MA_NOACTIVATE, matching the upstream companion window.
        if (message == 0x83)
            return IntPtr.Zero;
        if (message == 0x24 && l != IntPtr.Zero)
        {
            var limits = Marshal.PtrToStructure<Limits>(l);
            limits.MinimumTrack = new()
            {
                X = 1,
                Y = 1
            };
            Marshal.StructureToPtr(limits, l, false);
            return IntPtr.Zero;
        }
        var result = DefSubclassProc(w, message, p, l);
        // WM_DISPLAYCHANGE, WM_DPICHANGED or WM_SETTINGCHANGE (SPI_SETWORKAREA).
        if (message is 0x7e or 0x2e0 || (message == 0x1a && p.ToUInt64() == 0x2f))
            DisplayChanged?.Invoke();
        return result;
    }
    public static (int X, int Y) Cursor()
    {
        if (!GetCursorPos(out var p))
            throw new InvalidOperationException("无法读取指针位置。");
        return (p.X, p.Y);
    }
    public void SetBounds(int x, int y, int width, int height)
    {
        if (width is < 1 or > 1024 || height is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(width));
        var next = (x, y, width, height);
        if (bounds == next)
            return;
        var flags = bounds is { } previous && previous.Width == width && previous.Height == height ? 0x415u : 0x414u;
        if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, flags))
            throw new InvalidOperationException("无法更新悬浮窗尺寸。");
        bounds = next;
    }
    public void InvalidateGeometry()
    {
        bounds = null;
        clip = null;
    }
    public void Clip(int x, int y, int width, int height, int diameter)
    {
        if (x < 0 || y < 0 || width < 1 || height < 1 || width > 1024 || height > 1024 || x > 1024 - width || y > 1024 - height)
            throw new ArgumentOutOfRangeException(nameof(width));
        diameter = Math.Clamp(diameter, 1, Math.Min(width, height));
        var next = (x, y, width, height, diameter);
        if (clip == next)
            return;
        // The GDI rounded region bounds are one pixel smaller than the supplied extent.
        using var region = new Region(CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, diameter, diameter));
        var nextHitRegion = new Region(CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, diameter, diameter));
        if (region.IsInvalid || nextHitRegion.IsInvalid)
        {
            nextHitRegion.Dispose();
            throw new InvalidOperationException("无法创建悬浮窗外形。");
        }
        if (SetWindowRgn(window, region.DangerousGetHandle(), true) == 0)
        {
            nextHitRegion.Dispose();
            throw new InvalidOperationException("无法设置悬浮窗外形。");
        }
        region.SetHandleAsInvalid();
        hitRegion?.Dispose();
        hitRegion = nextHitRegion;
        clip = next;
    }
    public void Dispose()
    {
        RemoveWindowSubclass(window, callback, (UIntPtr)77);
        hitRegion?.Dispose();
        hitRegion = null;
    }
}
