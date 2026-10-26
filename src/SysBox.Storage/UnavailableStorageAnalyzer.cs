// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Storage;

public sealed class UnavailableStorageAnalyzer : IStorageAnalyzer
{
    public Task<Result<FolderSummary>> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<FolderSummary>.Failure(new(
            ErrorCodes.NotImplemented, "Storage analysis is not implemented (planned for M7).")));
    }
}
