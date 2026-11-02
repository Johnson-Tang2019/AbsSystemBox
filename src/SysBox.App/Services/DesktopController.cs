// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using SysBox.Core;
using SysBox.Native;
using SysBox.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace SysBox.App.Services;

public sealed class DesktopController : IDisposable
{
    private readonly DesktopPreferenceStore store;
    private readonly DesktopShell shell = new();
    private FloatingBallWindow? ball; private QuickSearchWindow? search;
    private readonly DispatcherTimer keepAlive = new() { Interval = TimeSpan.FromSeconds(30) };
    public DesktopPreferences Preferences { get; private set; } = new();
    public string Status { get; private set; } = "桌面入口尚未启动。";
    public bool Exiting
    {
        get; private set;
    }
    public bool CanKeepInTray => shell.TrayAvailable;
    public DesktopController(string directory)
    {
        store = new(directory);
        try
        {
            Preferences = store.Read();
        }
        catch (Exception e) { Status = e.Message; }
        shell.Invoked += action => App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
#if DEBUG
            try
            {
                var directory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
                if (directory is not null)
                    System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "desktop-events.txt"), action + " fullscreen=" + DesktopShell.ForeignFullscreen(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)) + Environment.NewLine);
            }
            catch { }
#endif
            switch (action)
            {
                case "main":
                    ShowMain();
                    break;
                case "search-hotkey":
                    if (!DesktopShell.ForeignFullscreen(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)))
                        ShowSearch();
                    break;
                case "search":
                    ShowSearch();
                    break;
                case "ball":
                    SetBall(ball is null);
                    break;
                case "exit":
                    Exit();
                    break;
                case "tray-unavailable":
                    Preferences = Preferences with
                    {
                        KeepInTray = false
                    };
                    Save();
                    Status = "托盘恢复失败，主窗口已恢复，关闭窗口将完全退出。";
                    ShowMain();
                    break;
            }
        });
        keepAlive.Tick += async (_, _) => { try { await App.Services.GetRequiredService<RpcClient>().CallAsync<System.Text.Json.JsonElement>("status", new { }); } catch { Status = "后台断开，将在下次操作时重新连接。"; } };
    }
    public async Task StartAsync()
    {
        try
        {
            await shell.StartAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/AppIcon.ico"));
            if (!shell.TrayAvailable && Preferences.KeepInTray)
            {
                Preferences = Preferences with
                {
                    KeepInTray = false
                };
                Save();
            }
            keepAlive.Start();
            ((ThemeService)App.Services.GetService(typeof(ThemeService))!).Apply((AppTheme)Preferences.Theme);
            var enabled = await shell.SetHotKeyAsync(Preferences.HotKey);
            Status = !shell.TrayAvailable ? "系统未允许托盘图标；悬浮球与快捷搜索仍可用，关闭窗口将完全退出。" : enabled ? "托盘已就绪；可在此启用悬浮球和快捷键。" : "快捷键被占用；请更换组合。";
            if (Preferences.FloatingBall)
                SetBall(true);
        }
        catch (Exception e) { Status = "桌面入口不可用：" + e.Message; }
    }
    public void ShowMain()
    {
        App.MainWindow.AppWindow.Show();
        App.MainWindow.Activate();
    }
    public void ShowSearch()
    {
        if (search is null)
        {
            search = new();
            search.Closed += (_, _) => search = null;
        }
        search.Activate();
    }
    public void SetBall(bool enabled)
    {
        Preferences = Preferences with
        {
            FloatingBall = enabled
        };
        Save();
        if (enabled && ball is null)
        {
            ball = new();
            ball.Closed += (_, _) => ball = null;
            ball.Activate();
        }
        if (!enabled)
        {
            ball?.Close();
            ball = null;
        }
    }
    public void SaveBallPosition(int x, int y)
    {
        Preferences = Preferences with
        {
            BallX = x,
            BallY = y
        };
        Save();
    }
    public void SaveTheme(int value)
    {
        Preferences = Preferences with
        {
            Theme = value
        };
        Save();
        var theme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        if (ball?.Content is FrameworkElement ballRoot)
            ballRoot.RequestedTheme = theme;
        if (search?.Content is FrameworkElement searchRoot)
            searchRoot.RequestedTheme = theme;
    }
    public async Task ConfigureAsync(int hotKey, bool keepInTray, bool floating)
    {
        try
        {
            if (!await shell.SetHotKeyAsync(hotKey))
            {
                Status = "快捷键已被其他应用占用，原设置保留。";
                return;
            }
            Preferences = Preferences with
            {
                HotKey = hotKey,
                KeepInTray = keepInTray && shell.TrayAvailable
            };
            SetBall(floating);
            Save();
            Status = shell.TrayAvailable ? "桌面设置已保存。完全退出会停止所有组件。" : "托盘不可用，已关闭托盘常驻；悬浮球设置已保存。";
        }
        catch (Exception e) { Status = "设置未生效：" + e.Message; }
    }
    private void Save()
    {
        try
        {
            store.Write(Preferences);
        }
        catch { Status = "无法保存桌面设置，原设置文件保留。"; }
    }
    public void Exit()
    {
        if (Exiting)
            return;
        Exiting = true;
        ball?.Close();
        search?.Close();
        App.MainWindow.Close();
    }
    public void Dispose()
    {
        Exiting = true;
        keepAlive.Stop();
        ball?.Close();
        search?.Close();
        ball = null;
        search = null;
        shell.Dispose();
    }
}
