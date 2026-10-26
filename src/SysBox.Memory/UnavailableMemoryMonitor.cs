// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Memory;

public sealed class UnavailableMemoryMonitor : IMemoryMonitor
{
    public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<MemorySnapshot>.Failure(new(
            ErrorCodes.NotImplemented, "Memory collection is not implemented (planned for M2).")));
    }
}
