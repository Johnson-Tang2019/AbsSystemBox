// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysBox.Native;

// All native windows, icons, hotkeys and menus are created and released on one owning thread.
public sealed class DesktopShell : IDisposable
{
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WindowProcedure Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr w, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr w, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Position; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr w, uint m, IntPtr p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr instance);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr w, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr w, uint m, IntPtr p, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr w, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr w, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int x, int y, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIconW(uint command, ref IconData data);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string label);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr w, IntPtr region, bool redraw);
    private sealed class IconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IconHandle(IntPtr h) : base(true) => SetHandle(h); protected override bool ReleaseHandle() => DestroyIcon(handle);
    }
    private sealed class MenuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MenuHandle(IntPtr h) : base(true) => SetHandle(h); protected override bool ReleaseHandle() => DestroyMenu(handle);
    }
    private sealed class RegionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RegionHandle(IntPtr h) : base(true) => SetHandle(h); protected override bool ReleaseHandle() => DeleteObject(handle);
    }
    private sealed class WindowHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public WindowHandle(IntPtr h) : base(true) => SetHandle(h); protected override bool ReleaseHandle() => DestroyWindow(handle);
    }
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowProcedure procedure;
    private readonly string className = "AbsSystemBox.Desktop." + Guid.NewGuid().ToString("N");
    private Thread? thread; private IntPtr window; private bool stopping; private int hotKeyId = 100, currentChoice; private uint taskbar;
    public event Action<string>? Invoked;
    public bool TrayAvailable
    {
        get; private set;
    }
    public DesktopShell() => procedure = Procedure;
    public Task StartAsync(string iconPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        thread = new Thread(() => Run(iconPath)) { IsBackground = true, Name = "SysBox desktop messages" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private void Run(string iconPath)
    {
        IconHandle? icon = null;
        WindowHandle? owner = null;
        IconData data = default;
        var module = GetModuleHandleW(null);
        try
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Name = className, Instance = module };
            if (RegisterClassExW(ref cls) == 0)
                throw new IOException("无法注册桌面消息窗口。");
            window = CreateWindowExW(0, className, "AbsSystemBox desktop", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            owner = new(window);
            if (window == IntPtr.Zero)
                throw new IOException("无法建立桌面消息窗口。");
            icon = new(LoadImageW(IntPtr.Zero, iconPath, 1, 32, 32, 0x10));
            if (icon.IsInvalid)
                throw new IOException("无法读取托盘图标。");
            data = new IconData { Size = (uint)Marshal.SizeOf<IconData>(), Window = window, Id = 1, Flags = 7, Callback = 0x8001, Icon = icon.DangerousGetHandle(), Tip = "AbsSystemBox · 右键打开菜单", Info = "", Title = "" };
            if (data.Size != 976)
                throw new IOException("托盘结构长度不匹配：" + data.Size);
            TrayAvailable = Shell_NotifyIconW(0, ref data);
            taskbar = RegisterWindowMessageW("TaskbarCreated");
            restoreIcon = () => TrayAvailable = Shell_NotifyIconW(0, ref data);
            started.TrySetResult();
            while (!stopping)
            {
                var result = GetMessageW(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                    break;
                DispatchMessageW(ref message);
            }
        }
        catch (Exception e) { started.TrySetException(e); }
        finally
        {
            if (window != IntPtr.Zero)
            {
                UnregisterHotKey(window, 100);
                UnregisterHotKey(window, 101);
                Shell_NotifyIconW(2, ref data);
                owner?.Dispose();
                window = IntPtr.Zero;
            }
            icon?.Dispose();
            UnregisterClassW(className, module);
        }
    }
    private Action? restoreIcon;
    private IntPtr Procedure(IntPtr w, uint m, IntPtr p, IntPtr l)
    {
        try
        {
            if (m == 0x8002)
            {
                while (commands.TryDequeue(out var action))
                    action();
                return IntPtr.Zero;
            }
            if (m == 0x8003)
            {
                stopping = true;
                return IntPtr.Zero;
            }
            if (taskbar != 0 && m == taskbar)
            {
                restoreIcon?.Invoke();
                if (!TrayAvailable)
                    Invoked?.Invoke("tray-unavailable");
                return IntPtr.Zero;
            }
            if (m == 0x312)
            {
                Invoked?.Invoke("search-hotkey");
                return IntPtr.Zero;
            }
            if (m == 0x8001)
            {
                if (l.ToInt64() == 0x202)
                    Invoked?.Invoke("main");
                if (l.ToInt64() == 0x205)
                {
                    using var menu = new MenuHandle(CreatePopupMenu());
                    string[] labels = ["打开主窗口", "快捷搜索", "显示 / 隐藏悬浮球", "完全退出"];
                    for (var i = 0; i < labels.Length; i++)
                        AppendMenuW(menu.DangerousGetHandle(), 0, (UIntPtr)(i + 1), labels[i]);
                    GetCursorPos(out var point);
                    SetForegroundWindow(window);
                    var selected = TrackPopupMenu(menu.DangerousGetHandle(), 0x180, point.X, point.Y, 0, window, IntPtr.Zero);
                    if (selected is >= 1 and <= 4)
                        Invoked?.Invoke(new[] { "main", "search", "ball", "exit" }[selected - 1]);
                }
                return IntPtr.Zero;
            }
        }
        catch { /* Exceptions must not cross the native callback boundary. */ }
        return DefWindowProcW(w, m, p, l);
    }
    public Task<bool> SetHotKeyAsync(int choice)
    {
        if (choice is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(choice));
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(() =>
        {
            if (choice == currentChoice)
            {
                result.TrySetResult(true);
                return;
            }
            if (choice == 0)
            {
                UnregisterHotKey(window, hotKeyId);
                currentChoice = 0;
                result.TrySetResult(true);
                return;
            }
            var next = hotKeyId == 100 ? 101 : 100;
            var key = choice switch
            {
                1 => 0x20u,
                2 => 0x51u,
                _ => 0x53u
            };
            if (!RegisterHotKey(window, next, 0x4003, key))
            {
                result.TrySetResult(false);
                return;
            }
            UnregisterHotKey(window, hotKeyId);
            hotKeyId = next;
            currentChoice = choice;
            result.TrySetResult(true);
        });
        if (window == IntPtr.Zero || !PostMessageW(window, 0x8002, IntPtr.Zero, IntPtr.Zero))
            result.TrySetException(new IOException("桌面入口不可用。"));
        return result.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    public static void RoundWindow(IntPtr window, int size)
    {
        using var region = new RegionHandle(CreateEllipticRgn(0, 0, size, size));
        if (!region.IsInvalid && SetWindowRgn(window, region.DangerousGetHandle(), true) != 0)
            region.SetHandleAsInvalid();
    }
    public static bool ForeignFullscreen(IntPtr ownWindow)
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        if (foregroundProcess == Environment.ProcessId)
            return false;
        if (foreground == IntPtr.Zero || foreground == ownWindow)
            return false;
        var name = new System.Text.StringBuilder(256);
        GetClassNameW(foreground, name, 256);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(foreground, out var rect) || !GetMonitorInfoW(MonitorFromWindow(foreground, 2), ref info))
            return false;
        return Math.Abs(rect.Left - info.Monitor.Left) <= 1 && Math.Abs(rect.Top - info.Monitor.Top) <= 1 && Math.Abs(rect.Right - info.Monitor.Right) <= 1 && Math.Abs(rect.Bottom - info.Monitor.Bottom) <= 1;
    }
    public void Dispose()
    {
        if (window != IntPtr.Zero)
        {
            PostMessageW(window, 0x1f, IntPtr.Zero, IntPtr.Zero);
            PostMessageW(window, 0x8003, IntPtr.Zero, IntPtr.Zero);
        }
        thread?.Join(1000);
    }
}
