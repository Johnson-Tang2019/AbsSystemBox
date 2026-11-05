// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed record DashboardCapacity(decimal TotalBytes, decimal AvailableBytes, int ReadyVolumes, int ReportedVolumes)
{
    public double? UsedPercent => TotalBytes > 0 ? (double)((TotalBytes - AvailableBytes) * 100m / TotalBytes) : null;
    public static DashboardCapacity FromVolumes(IEnumerable<VolumeCapacity> volumes)
    {
        var reported = volumes.ToArray();
        var ready = reported.Where(v => v.TotalBytes is > 0 && v.AvailableBytes is >= 0 && v.AvailableBytes <= v.TotalBytes).ToArray();
        // Aggregate bytes before computing a percentage, rather than averaging unequal volumes.
        return new(ready.Sum(v => (decimal)v.TotalBytes!.Value), ready.Sum(v => (decimal)v.AvailableBytes!.Value), ready.Length, reported.Length);
    }
}
