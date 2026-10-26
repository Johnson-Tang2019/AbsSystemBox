// SPDX-License-Identifier: GPL-3.0-or-later
#if DEBUG
using SysBox.Abstractions;

namespace SysBox.App.Services;

internal sealed class DemoMemoryMonitor : IMemoryMonitor
{
    private readonly DemoDashboardClient client = new();
    public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken ct) => client.ReadAsync(ct);
}

internal sealed class DemoProcessMemoryMonitor : IProcessMemoryMonitor
{
    public async Task<Result<ProcessMemorySnapshot>> ReadAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        ProcessMemoryEntry[] entries = [new(4242, "示例进程（非本机）", null, null, 512UL << 20, 768UL << 20)];
        return Result<ProcessMemorySnapshot>.Success(new(DateTimeOffset.UtcNow, entries,
            1, 1, 0, 0, 0, 0, true, DataCompleteness.Complete));
    }
}
#endif
