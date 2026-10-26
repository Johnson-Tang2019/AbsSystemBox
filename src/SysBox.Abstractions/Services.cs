// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public interface IMemoryMonitor
{
    Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken);
}

public interface IFileSearchService
{
    // Streaming failures use SysBoxOperationException; cancellation uses OperationCanceledException.
    IAsyncEnumerable<FileSearchHit> SearchAsync(FileSearchRequest request, CancellationToken cancellationToken);
}

public interface IStorageAnalyzer
{
    Task<Result<FolderSummary>> AnalyzeAsync(string path, CancellationToken cancellationToken);
}
