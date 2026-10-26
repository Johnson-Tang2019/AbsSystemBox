// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Search;

public sealed class UnavailableFileSearchService : IFileSearchService
{
    public IAsyncEnumerable<FileSearchHit> SearchAsync(FileSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new SysBoxOperationException(new(
            ErrorCodes.NotImplemented, "File indexing and search are not implemented (planned for M4/M5)."));
    }
}
