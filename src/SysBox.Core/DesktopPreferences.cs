// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace SysBox.Core;

public sealed record DesktopPreferences(bool FloatingBall = false, bool KeepInTray = false, int HotKey = 0, int Theme = 0, int? BallX = null, int? BallY = null);
public sealed class DesktopPreferenceStore(string directory)
{
    private readonly string path = Path.Combine(directory, "desktop.json");
    public DesktopPreferences Read()
    {
        if (!File.Exists(path))
            return new();
        var file = new FileInfo(path);
        if (file.Length > 4096)
            throw new IOException("桌面设置过大，原文件已保留。");
        var result = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path)) ?? throw new IOException("桌面设置损坏。");
        if (result.HotKey is < 0 or > 3 || result.Theme is < 0 or > 2)
            throw new IOException("桌面设置无效。");
        return result;
    }
    public void Write(DesktopPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
        File.Move(temporary, path, true);
    }
}

public static class DesktopBounds
{
    public static (int X, int Y) Clamp(int x, int y, int size, int left, int top, int width, int height)
        => (Math.Clamp(x, left, Math.Max(left, left + width - size)), Math.Clamp(y, top, Math.Max(top, top + height - size)));
}
