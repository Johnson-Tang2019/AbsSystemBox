// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Core;
namespace SysBox.UnitTests;

public sealed class DesktopFeaturesTests
{
    [Fact]
    public void FloatingBallClampsNegativeMonitorsAndTinyWorkAreas()
    {
        Assert.Equal((-1920, 0), DesktopBounds.Clamp(-3000, -20, 76, -1920, 0, 1920, 1080));
        Assert.Equal((-76, 1004), DesktopBounds.Clamp(100, 2000, 76, -1920, 0, 1920, 1080));
        Assert.Equal((10, 20), DesktopBounds.Clamp(50, 60, 76, 10, 20, 20, 20));
    }
    [Fact]
    public void DesktopSettingsAreOptInAndRoundTripWithoutTouchingOtherData()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-Desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DesktopPreferenceStore(root);
            Assert.False(store.Read().FloatingBall);
            Assert.False(store.Read().KeepInTray);
            File.WriteAllText(Path.Combine(root, "rule-state.json"), "preserved");
            var preferences = new DesktopPreferences(true, true, 2, 1, -1800, 40);
            store.Write(preferences);
            Assert.Equal(preferences, store.Read());
            Assert.Equal("preserved", File.ReadAllText(Path.Combine(root, "rule-state.json")));
            File.WriteAllText(Path.Combine(root, "desktop.json"), "broken");
            Assert.ThrowsAny<Exception>(() => store.Read());
        }
        finally { Directory.Delete(root, true); }
    }
}
