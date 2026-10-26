// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SysBox.Presentation;

public static class MemoryFormatting
{
    public static string GiB(ulong? bytes) => bytes is { } value ? $"{value / 1073741824.0:F1} GiB" : "—";
    public static string MiB(ulong bytes) => $"{bytes / 1048576.0:N1} MiB";
    public static double? Percentage(ulong used, ulong total) => total > 0 && used <= total ? used * 100.0 / total : null;
    public static double? PhysicalPercentage(MemorySnapshot? snapshot) => snapshot is { } data && data.AvailablePhysicalBytes <= data.TotalPhysicalBytes
        ? Percentage(data.TotalPhysicalBytes - data.AvailablePhysicalBytes, data.TotalPhysicalBytes) : null;
    public static string Percent(double? percentage) => percentage is { } value ? $"{value:F1}%" : "—";
}

public sealed record MemoryHistoryPoint(DateTimeOffset TimestampUtc, double UsedPercent);

public sealed class MemoryHistory(int capacity = 60)
{
    private readonly Queue<MemoryHistoryPoint> samples = new(ValidateCapacity(capacity));
    public int Capacity { get; } = ValidateCapacity(capacity);
    public IReadOnlyList<MemoryHistoryPoint> Points => samples.ToArray();
    public void Add(MemorySnapshot snapshot)
    {
        if (MemoryFormatting.PhysicalPercentage(snapshot) is not { } percentage) return;
        if (samples.Count == Capacity) samples.Dequeue();
        samples.Enqueue(new(snapshot.TimestampUtc, percentage));
    }
    public void Clear() => samples.Clear();
    private static int ValidateCapacity(int capacity) => capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
}

public sealed class ProcessMemoryRow : ObservableObject
{
    public ProcessMemoryEntry? Entry { get; private set; }
    public string Name => Entry?.Name ?? "—";
    public string ProcessId => Entry?.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public string WorkingSet => Entry is { } entry ? MemoryFormatting.MiB(entry.WorkingSetBytes) : "—";
    public string PrivateBytes => Entry is { } entry ? MemoryFormatting.MiB(entry.PrivateBytes) : "—";
    public string Identity => Entry is null ? "—" : Entry.StartTimeUtc is { } time ? time.ToLocalTime().ToString("HH:mm:ss") : "未验证";
    public string Details => Entry is { } entry
        ? $"路径：{entry.ImagePath ?? "无法获取"}\n启动时间（UTC）：{entry.StartTimeUtc?.ToString("O") ?? "无法验证"}" : "尚未读取进程数据。";

    // Reuse a bounded UI slot, but replace the entire immutable process identity atomically.
    public void Update(ProcessMemoryEntry? entry)
    {
        if (Equals(Entry, entry)) return;
        Entry = entry;
        OnPropertyChanged(string.Empty);
    }
}
