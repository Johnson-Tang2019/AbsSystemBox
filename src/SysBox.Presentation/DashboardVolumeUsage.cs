// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed record DashboardVolumeUsage(VolumeCapacity Volume)
{
    public string Title => string.IsNullOrWhiteSpace(Volume.FileSystem) ? Volume.Root : $"{Volume.Root} · {Volume.FileSystem}";
    public double? UsedPercent => Volume.TotalBytes is > 0 && Volume.AvailableBytes is >= 0 && Volume.AvailableBytes <= Volume.TotalBytes
        ? (double)((Volume.TotalBytes.Value - (decimal)Volume.AvailableBytes.Value) * 100m / Volume.TotalBytes.Value) : null;
    public bool HasReading => UsedPercent.HasValue;
    public double IndicatorValue => UsedPercent ?? 0;
    public string Detail => UsedPercent is { } used
        ? $"已用 {used:F1}% · 可用 {SizeFormatting.Format(Volume.AvailableBytes!.Value)} / {SizeFormatting.Format(Volume.TotalBytes!.Value)}"
        : $"{(Volume.TotalBytes is not null && Volume.AvailableBytes is not null ? "容量读数无效" : string.IsNullOrWhiteSpace(Volume.State) ? "容量暂不可用" : Volume.State)} · 使用率未知";
    public string ProgressName => $"{Volume.Root} {Detail}";
    // Keep unavailable and not-ready drives; they still need a visible status row.
    public static DashboardVolumeUsage[] FromVolumes(IEnumerable<VolumeCapacity> volumes) => volumes.Select(v => new DashboardVolumeUsage(v)).ToArray();
}
