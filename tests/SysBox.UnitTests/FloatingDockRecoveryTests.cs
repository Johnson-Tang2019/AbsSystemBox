// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Core;

namespace SysBox.UnitTests;

public sealed class FloatingDockRecoveryTests
{
    [Fact]
    public void UpstreamTransitionsRestoreOnlySavedDockingDetachOnMoveAndCollapseOnCommit()
    {
        var area = new BallRectangle(-1920, 0, 1920, 1080);
        var state = new FluentBallState();
        state.Restore(null, null, 1, area);
        Assert.Equal(BallEdge.None, state.Edge);
        Assert.False(state.Collapsed);
        state.Hover(false);
        Assert.False(state.Collapsed);
        state.Restore(-1920, 300, 1, area);
        Assert.Equal(BallEdge.Left, state.Edge);
        Assert.True(state.Collapsed);
        var anchor = (state.AnchorX, state.AnchorY);
        state.Hover(true);
        Assert.False(state.Collapsed);
        state.Hover(false);
        Assert.True(state.Collapsed);
        Assert.Equal(anchor, (state.AnchorX, state.AnchorY));
        state.Move(-800, 500);
        Assert.Equal(BallEdge.None, state.Edge);
        state.Commit(1, area);
        Assert.Equal(BallEdge.None, state.Edge);
        state.Move(-1920 + 32, 500);
        state.Commit(1, area);
        Assert.Equal(BallEdge.Left, state.Edge);
        Assert.True(state.Collapsed);
        state.Move(-9999, 500);
        state.Commit(1, area);
        Assert.Equal(BallEdge.Left, state.Edge);
        state.Reflow(1, area);
        Assert.Equal(-1888, state.AnchorX);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Move(double.NaN, 0));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void RepeatedCollapseRetainsCompositorBoundsAndRestrictsOnlyTheVisibleRegion(double scale)
    {
        foreach (var edge in new[] { BallEdge.Left, BallEdge.Right, BallEdge.Top, BallEdge.Bottom })
        {
            var expanded = FloatingBallLayout.Frame(-800, 500, edge, false, scale, -1920, 0, 1920, 1080);
            for (var cycle = 0; cycle < 100; cycle++)
            {
                var collapsed = FloatingBallLayout.Frame(-800, 500, edge, true, scale, -1920, 0, 1920, 1080);
                Assert.Equal(expanded.Window, collapsed.Window);
                Assert.Equal((int)Math.Ceiling(64 * scale), collapsed.Window.Width);
                Assert.Equal(collapsed.Window.Width, collapsed.Window.Height);
                Assert.InRange(collapsed.Region.X, 0, collapsed.Window.Width - collapsed.Region.Width);
                Assert.InRange(collapsed.Region.Y, 0, collapsed.Window.Height - collapsed.Region.Height);
                Assert.Equal((int)Math.Ceiling(14 * scale), Math.Min(collapsed.Region.Width, collapsed.Region.Height));
                if (edge == BallEdge.Right)
                    Assert.Equal(collapsed.Window.Width, collapsed.Region.X + collapsed.Region.Width);
                if (edge == BallEdge.Bottom)
                    Assert.Equal(collapsed.Window.Height, collapsed.Region.Y + collapsed.Region.Height);
                Assert.Equal(expanded, FloatingBallLayout.Frame(-800, 500, edge, false, scale, -1920, 0, 1920, 1080));
            }
        }
    }
}
