// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Memory;
using SysBox.Native;

namespace SysBox.UnitTests;

[CollectionDefinition("Windows read-only", DisableParallelization = true)]
public sealed class WindowsReadOnlyCollection;

[Collection("Windows read-only")]
public sealed class WindowsMemoryIntegrationTests
{
    [Fact]
    public async Task NativeReadReturnsPhysicalCommitAndCurrentProcess()
    {
        // Non-Windows hosts exercise deterministic fakes, not Windows DLLs.
        if (!OperatingSystem.IsWindows()) return;
        var api = new WindowsMemoryApi();
        var memory = (await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None)).Value;
        Assert.True(memory.TotalPhysicalBytes > 0);
        Assert.InRange(memory.AvailablePhysicalBytes, 0UL, memory.TotalPhysicalBytes);
        Assert.NotNull(memory.CommitTotalBytes);
        Assert.NotNull(memory.CommitLimitBytes);
        Assert.True(memory.CommitTotalBytes <= memory.CommitLimitBytes);
        using var current = Process.GetCurrentProcess();
        var sample = api.ReadProcess((uint)current.Id);
        Assert.True(sample.WorkingSetBytes > 0);
        Assert.True(sample.PrivateBytes > 0);
        Assert.NotNull(sample.ImagePath);
        Assert.NotNull(sample.StartTimeUtc);
        Assert.InRange(Math.Abs((sample.StartTimeUtc.Value - current.StartTime.ToUniversalTime()).TotalMilliseconds), 0, 1);
        var processes = await new WindowsProcessMemoryMonitor(api).ReadAsync(CancellationToken.None);
        Assert.True(processes.IsSuccess);
        Assert.True(processes.Value.EnumeratedCount > 0);
        Assert.True(processes.Value.ReadableCount > 0);
        Assert.Equal(processes.Value.EnumeratedCount,
            processes.Value.ReadableCount + processes.Value.AccessDeniedCount + processes.Value.UnavailableCount
            + processes.Value.OtherErrorCount + processes.Value.BudgetSkippedCount);
    }

    [Fact]
    public void RepeatedHandleReadsDoNotAccumulateProcessHandles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var api = new WindowsMemoryApi();
        using var current = Process.GetCurrentProcess();
        for (var index = 0; index < 5; index++) api.ReadProcess((uint)current.Id);
        current.Refresh();
        var before = current.HandleCount;
        for (var index = 0; index < 100; index++) api.ReadProcess((uint)current.Id);
        current.Refresh();
        Assert.InRange(current.HandleCount - before, -10, 3);
    }
}
