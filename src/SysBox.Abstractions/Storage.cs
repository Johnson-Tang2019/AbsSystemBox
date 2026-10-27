// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record FileSizeMetadata(long LogicalBytes, long AllocatedBytes, uint LinkCount, DateTimeOffset TimestampUtc);
public interface IFileSizeReader
{
    Result<FileSizeMetadata> Read(string path);
}
public enum StorageSort { LogicalBytes, FileCount }
public sealed record StorageItem(string Path, string Name, bool IsDirectory, long LogicalBytes,
    long KnownAllocatedBytes, long UnknownAllocatedFiles, long FileCount, long FolderCount, long HardLinkedEntries,
    DataCompleteness Completeness, DateTimeOffset TimestampUtc);
public sealed record StorageView(StorageItem Summary, IReadOnlyList<StorageItem> Children, bool HasMore,
    long Generation, string ScopeId, string ScopeRoot, long ScopeSkippedEntries, string Notice);
public interface IStorageBrowser
{
    Task<IndexProgress> AnalyzeAsync(string path, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token);
    Task<StorageView> ReadAsync(string path, StorageSort sort, int offset, int limit, CancellationToken token,
        string? scopeRoot = null, long? expectedGeneration = null);
}
