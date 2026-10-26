// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

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
}
