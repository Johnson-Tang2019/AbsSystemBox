// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public static class ApiContract
{
    public const string Version = "1.0";
}

public enum DataCompleteness { Unknown, Partial, Complete }

// IDs use strings so JSON clients cannot lose 64-bit identity precision.
public sealed record VolumeInfo(
    string VolumeId,
    IReadOnlyList<string> MountPoints,
    string FileSystem,
    bool IsReady,
    bool SupportsUsn);

public sealed record FileEntry(
    string VolumeId,
    string? FileId,
    string? ParentFileId,
    string Path,
    string Name,
    bool IsDirectory,
    ulong? LogicalSizeBytes,
    DateTimeOffset? LastWriteTimeUtc);

// Commit values must come from a commit accounting API, not physical RAM arithmetic.
public sealed record MemorySnapshot(
    DateTimeOffset TimestampUtc,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong? CommitTotalBytes,
    ulong? CommitLimitBytes,
    DataCompleteness Completeness,
    string ApiVersion = ApiContract.Version);

public sealed record FileSearchRequest
{
    public string Text { get; }
    public int Limit { get; }
    public bool IncludeHidden { get; }

    public FileSearchRequest(string text, int limit = 100, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        Text = text;
        Limit = limit;
        IncludeHidden = includeHidden;
    }
}

public sealed record FileSearchHit(
    FileEntry Entry,
    DateTimeOffset IndexedAtUtc,
    DataCompleteness Completeness,
    string ApiVersion = ApiContract.Version);

// Logical bytes count directory entries. Allocated size/hard-link deduplication are separate future metrics.
public sealed record FolderSummary(
    string VolumeId,
    string? FileId,
    string Path,
    ulong LogicalSizeBytes,
    long FileCount,
    long FolderCount,
    long SkippedEntryCount,
    DataCompleteness Completeness,
    DateTimeOffset TimestampUtc,
    string ApiVersion = ApiContract.Version);
