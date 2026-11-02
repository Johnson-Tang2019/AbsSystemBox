// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;
namespace SysBox.Native;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "AbsSystemBox";
    private static string Command => "\"" + Path.Combine(AppContext.BaseDirectory, "SysBox.App.exe") + "\"";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SysBox.App.exe");
        if (!File.Exists(path))
            throw new IOException("应用程序入口缺失。");
        using var key = Registry.CurrentUser.CreateSubKey(Key, true);
        var previous = key.GetValue(Name);
        if (previous is not null && (previous is not string command || !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("已有其他位置或格式的自启动项，请先从旧版本设置中关闭。原项已保留。");
        if (enabled)
            key.SetValue(Name, Command, RegistryValueKind.String);
        else
            key.DeleteValue(Name, false);
    }
}
