// SPDX-License-Identifier: GPL-3.0-or-later
// Geometry follows Fluent Reduct's 64-DIP ball / 14x56 edge bar; native WinUI rendering is independent.
namespace SysBox.Core;

public enum BallEdge
{
    None, Left, Right, Top, Bottom
}
public sealed record BallRectangle(int X, int Y, int Width, int Height);
public sealed record FloatingBallFrame(BallRectangle Window, BallRectangle Region);
public static class FloatingBallLayout
{
    public static FloatingBallFrame Frame(double cx, double cy, BallEdge edge, bool collapsed, double scale, int left, int top, int width, int height)
    {
        // Keep the compositor surface at ball size; only the visible/hit-test region changes.
        var window = Arrange(cx, cy, edge, false, scale, left, top, width, height);
        if (!collapsed || edge == BallEdge.None)
            return new(window, new(0, 0, window.Width, window.Height));
        var vertical = edge is BallEdge.Left or BallEdge.Right;
        var w = (int)Math.Ceiling((vertical ? 14 : 56) * scale);
        var h = (int)Math.Ceiling((vertical ? 56 : 14) * scale);
        var x = edge == BallEdge.Left ? 0 : edge == BallEdge.Right ? window.Width - w : (window.Width - w) / 2;
        var y = edge == BallEdge.Top ? 0 : edge == BallEdge.Bottom ? window.Height - h : (window.Height - h) / 2;
        return new(window, new(x, y, w, h));
    }
    public static BallRectangle Arrange(double cx, double cy, BallEdge edge, bool collapsed, double scale, int left, int top, int width, int height)
    {
        if (!double.IsFinite(scale) || scale is < 0.5 or > 5 || width < 1 || height < 1)
            throw new ArgumentException("悬浮窗显示范围无效。");
        var vertical = edge is BallEdge.Left or BallEdge.Right;
        var w = (int)Math.Ceiling((collapsed ? (vertical ? 14 : 56) : 64) * scale);
        var h = (int)Math.Ceiling((collapsed ? (vertical ? 56 : 14) : 64) * scale);
        var x = (int)Math.Round(cx - w / 2.0);
        var y = (int)Math.Round(cy - h / 2.0);
        if (edge == BallEdge.Left)
            x = left;
        if (edge == BallEdge.Right)
            x = left + width - w;
        if (edge == BallEdge.Top)
            y = top;
        if (edge == BallEdge.Bottom)
            y = top + height - h;
        return new(Math.Clamp(x, left, Math.Max(left, left + width - w)), Math.Clamp(y, top, Math.Max(top, top + height - h)), w, h);
    }
    public static BallEdge Detect(double cx, double cy, double scale, int left, int top, int width, int height)
    {
        var radius = 32 * scale;
        var distances = new[] { (BallEdge.Left, Math.Abs(cx - radius - left)), (BallEdge.Right, Math.Abs(left + width - cx - radius)), (BallEdge.Top, Math.Abs(cy - radius - top)), (BallEdge.Bottom, Math.Abs(top + height - cy - radius)) };
        var closest = distances.MinBy(p => p.Item2);
        return closest.Item2 <= 28 * scale ? closest.Item1 : BallEdge.None;
    }
}
