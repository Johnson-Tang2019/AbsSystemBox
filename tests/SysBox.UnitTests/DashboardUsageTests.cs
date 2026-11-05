// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class DashboardUsageTests
{
    [Fact]
    public void DiskProgressWeightsVolumeCapacityAndMarksUnreadableVolumes()
    {
        var usage = DashboardCapacity.FromVolumes([
            new("C:", "NTFS", 1000, 500, "Ready"), new("D:", "NTFS", 9000, 900, "Ready"),
            new("E:", "", null, null, "Unavailable"), new("F:", "", 100, 101, "Invalid")]);
        Assert.Equal(86, usage.UsedPercent);
        Assert.Equal(10_000m, usage.TotalBytes);
        Assert.Equal(1_400m, usage.AvailableBytes);
        Assert.Equal(2, usage.ReadyVolumes);
        Assert.Equal(4, usage.ReportedVolumes);
        Assert.Null(DashboardCapacity.FromVolumes([new("E:", "", null, null, "Unavailable")]).UsedPercent);
        Assert.Null(DashboardCapacity.FromVolumes([]).UsedPercent);
    }
    [Fact]
    public void ProgressPreservesEmptyFullAndLargeVolumesWithoutOverflow()
    {
        Assert.Equal(0, DashboardCapacity.FromVolumes([new("C:", "", 100, 100, "Ready")]).UsedPercent);
        Assert.Equal(100, DashboardCapacity.FromVolumes([new("C:", "", 100, 0, "Ready")]).UsedPercent);
        var large = DashboardCapacity.FromVolumes([new("C:", "", long.MaxValue, 0, "Ready"), new("D:", "", long.MaxValue, long.MaxValue, "Ready")]);
        Assert.Equal(50, large.UsedPercent);
        Assert.Null(MemoryFormatting.PhysicalPercentage(new(DateTimeOffset.UtcNow, 100, 101, null, null, DataCompleteness.Partial)));
    }
}
