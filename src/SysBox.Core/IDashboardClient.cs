// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Core;

public interface IDashboardClient
{
    bool IsDemo { get; }
    Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class DashboardClient(IMemoryMonitor monitor) : IDashboardClient
{
    public bool IsDemo => false;
    public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken) =>
        monitor.ReadAsync(cancellationToken);
}
