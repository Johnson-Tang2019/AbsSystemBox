// SPDX-License-Identifier: GPL-3.0-or-later
#if DEBUG
using SysBox.Abstractions;
using SysBox.Core;

namespace SysBox.App.Services;

// Opt-in only: the Release binary does not contain this type or its sample values.
internal sealed class DemoDashboardClient : IDashboardClient
{
    public bool IsDemo => true;

    public async Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return Result<MemorySnapshot>.Success(new(
            DateTimeOffset.UtcNow, 16UL << 30, 6UL << 30, 12UL << 30, 24UL << 30,
            DataCompleteness.Complete));
    }
}
#endif
