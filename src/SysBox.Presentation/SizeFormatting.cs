// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public static class SizeFormatting
{
    // SI labels use decimal factors. Raw index byte counts remain unchanged.
    public static string Format(long bytes)
    {
        var value = Math.Abs((decimal)bytes);
        var divisor = value >= 1_000_000_000 ? 1_000_000_000m : value >= 1_000_000 ? 1_000_000m : value >= 1_000 ? 1_000m : 1m;
        var unit = divisor switch
        {
            1_000_000_000m => "GB",
            1_000_000m => "MB",
            1_000m => "KB",
            _ => "B"
        };
        return (bytes / divisor).ToString(divisor == 1 ? "N0" : "N2", CultureInfo.CurrentCulture) + " " + unit;
    }
}

public sealed record VolumeCapacityRow(VolumeCapacity Value)
{
    public override string ToString() => (Value.TotalBytes is { } total && Value.AvailableBytes is { } free
        ? $"{Value.Root} · {SizeFormatting.Format(free)} / {SizeFormatting.Format(total)} 可用 · {Value.FileSystem}"
        : $"{Value.Root} · {Value.State}") + (Value.IndexedScopeCount is { } count ? $" · 已选索引范围 {count}" : " · 索引状态未知");
}
