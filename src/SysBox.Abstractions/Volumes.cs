// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record VolumeCapacity(string Root, string FileSystem, long? TotalBytes, long? AvailableBytes, string State, int? IndexedScopeCount = null)
{
    public override string ToString() => (TotalBytes is { } total && AvailableBytes is { } free ? $"{Root} · {free / 1073741824.0:F1} / {total / 1073741824.0:F1} GiB 可用 · {FileSystem}" : $"{Root} · {State}") + (IndexedScopeCount is { } count ? $" · 已选索引范围 {count}" : " · 索引状态未知");
}
