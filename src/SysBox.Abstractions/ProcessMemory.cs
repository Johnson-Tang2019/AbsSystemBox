// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum ProcessMemorySort
{
    WorkingSet, PrivateCommit
}
public static class ProcessMemoryOrdering
{
    public static IOrderedEnumerable<ProcessMemoryEntry> Order(IEnumerable<ProcessMemoryEntry> entries, ProcessMemorySort sort)
    {
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        return entries.OrderByDescending(e => sort == ProcessMemorySort.WorkingSet ? e.WorkingSetBytes : e.PrivateBytes).ThenBy(e => e.ProcessId);
    }
}

public sealed record ProcessMemoryEntry(
    uint ProcessId, string Name, string? ImagePath, DateTimeOffset? StartTimeUtc,
    ulong WorkingSetBytes, ulong PrivateBytes)
{
    public bool IsIdentityVerified => StartTimeUtc is not null;
}

// Entries are the highest working sets, not an additive decomposition of physical RAM.
public sealed record ProcessMemorySnapshot(
    DateTimeOffset TimestampUtc, IReadOnlyList<ProcessMemoryEntry> Entries,
    int EnumeratedCount, int ReadableCount, int AccessDeniedCount, int UnavailableCount,
    int OtherErrorCount, int BudgetSkippedCount, bool EnumerationComplete,
    DataCompleteness Completeness, string ApiVersion = ApiContract.Version)
{
    public const int MaximumEntries = 200;
}

public interface IProcessMemoryMonitor
{
    Task<Result<ProcessMemorySnapshot>> ReadAsync(CancellationToken cancellationToken);
    async Task<Result<ProcessMemorySnapshot>> ReadAsync(ProcessMemorySort sort, CancellationToken token)
    {
        var result = await ReadAsync(token);
        return result.IsSuccess ? Result<ProcessMemorySnapshot>.Success(result.Value with
        {
            Entries = ProcessMemoryOrdering.Order(result.Value.Entries, sort).ToArray()
        }) : result;
    }
}
