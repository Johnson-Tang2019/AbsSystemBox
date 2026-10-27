// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum IndexRunState { Running, Paused, Complete, Partial, Cancelled, Failed, LimitReached }

public sealed record IndexProgress(string Root, IndexRunState State, long Files, long Directories,
    long AccessDenied, long IoErrors, long ReparsePoints, long Excluded, long LogicalBytes,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, string? Error = null);

public sealed record IndexRoot(string Root, long Generation, IndexProgress Progress);

public sealed record IndexOptions
{
    public int MaxEntries { get; init; } = 1_000_000;
    public int MaxDepth { get; init; } = 512;
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
}

public interface IIndexControl
{
    bool IsPaused { get; }
    void Pause();
    void Resume();
    Task WaitAsync(CancellationToken cancellationToken);
}

public interface IFileIndexService
{
    Task<IndexProgress> ScanAsync(string root, IndexOptions options, IIndexControl control,
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<IndexRoot>> ReadRootsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FileEntry>> ReadEntriesAsync(string root, int limit, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
