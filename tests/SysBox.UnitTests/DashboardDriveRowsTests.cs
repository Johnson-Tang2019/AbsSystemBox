// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class DashboardDriveRowsTests
{
    [Fact]
    public void EveryReportedDriveGetsItsOwnRowIncludingNotReadyAndUnreadableDrives()
    {
        VolumeCapacity[] volumes = [new("C:", "NTFS", 1000, 500, "已就绪"), new("D:", "NTFS", 9000, 900, "已就绪"), new("E:", "", null, null, "未就绪")];
        var rows = DashboardVolumeUsage.FromVolumes(volumes);
        Assert.Equal(3, rows.Length);
        Assert.Equal(50, rows[0].UsedPercent);
        Assert.Equal(90, rows[1].UsedPercent);
        Assert.Equal("E:", rows[2].Title);
        Assert.False(rows[2].HasReading);
        Assert.Null(rows[2].UsedPercent);
        Assert.Contains("未就绪", rows[2].Detail);
        Assert.Contains("使用率未知", rows[2].Detail);
        Assert.DoesNotContain("0%", rows[2].Detail);
        Assert.Empty(DashboardVolumeUsage.FromVolumes([]));
    }
    [Fact]
    public void PerDrivePercentKeepsFullAndEmptyVolumesButRejectsInvalidCapacity()
    {
        var rows = DashboardVolumeUsage.FromVolumes([new("C:", "NTFS", 100, 100, "已就绪"), new("D:", "NTFS", long.MaxValue, 0, "已就绪"), new("E:", "", 10, 11, "已就绪")]);
        Assert.Equal(0, rows[0].UsedPercent);
        Assert.Equal(100, rows[1].UsedPercent);
        Assert.Null(rows[2].UsedPercent);
        Assert.Contains("读数无效", rows[2].Detail);
        Assert.Contains("B", rows[0].Detail);
        Assert.StartsWith("D:", rows[1].ProgressName);
    }
}
