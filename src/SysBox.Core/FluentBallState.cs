// SPDX-License-Identifier: GPL-3.0-or-later
// C# port of s3hq4y/fluent-reduct src/ui/main/ball.ts, commit 64fa85f.
// Upstream GPL source and attribution: licenses/third-party/FluentReduct-64fa85f.
namespace SysBox.Core;

public sealed class FluentBallState
{
    public double AnchorX
    {
        get; private set;
    }
    public double AnchorY
    {
        get; private set;
    }
    public BallEdge Edge
    {
        get; private set;
    }
    public bool Collapsed
    {
        get; private set;
    }

    public void Restore(int? savedX, int? savedY, double scale, BallRectangle area)
    {
        var saved = savedX.HasValue && savedY.HasValue;
        // Existing SysBox preferences store the visible 64-DIP ball origin, without Electron's shadow padding.
        AnchorX = saved ? savedX!.Value + 32 * scale : area.X + area.Width - (24 + 32) * scale;
        AnchorY = saved ? savedY!.Value + 32 * scale : area.Y + area.Height - (24 + 32) * scale;
        Edge = saved ? DetectEdge(scale, area) : BallEdge.None;
        Collapsed = Edge != BallEdge.None;
    }
    public void Move(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x));
        AnchorX = x;
        AnchorY = y;
        Edge = BallEdge.None;
        Collapsed = false;
    }
    public void Commit(double scale, BallRectangle area)
    {
        Edge = DetectEdge(scale, area);
        Collapsed = Edge != BallEdge.None;
    }
    public void Hover(bool inside)
    {
        if (Edge != BallEdge.None)
            Collapsed = !inside;
    }
    public void Reflow(double scale, BallRectangle area)
    {
        AnchorX = ClampAxis(AnchorX, area.X + 32 * scale, area.X + area.Width - 32 * scale);
        AnchorY = ClampAxis(AnchorY, area.Y + 32 * scale, area.Y + area.Height - 32 * scale);
        if (Edge == BallEdge.None)
            Edge = DetectEdge(scale, area);
    }
    private BallEdge DetectEdge(double scale, BallRectangle area)
    {
        var half = 32 * scale;
        var distances = new[] {
            (BallEdge.Left, AnchorX - area.X - half),
            (BallEdge.Right, area.X + area.Width - AnchorX - half),
            (BallEdge.Top, AnchorY - area.Y - half),
            (BallEdge.Bottom, area.Y + area.Height - AnchorY - half)
        };
        var closest = distances.MinBy(pair => pair.Item2);
        return closest.Item2 <= 28 * scale ? closest.Item1 : BallEdge.None;
    }
    private static double ClampAxis(double value, double min, double max) => Math.Floor(Math.Clamp(value, min, Math.Max(min, max)) + 0.5);
}
