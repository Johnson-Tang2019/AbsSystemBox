// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using SysBox.Abstractions;
using SysBox.Memory;
using SysBox.Native;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class MemoryCollectionTests
{
    [Fact]
    public void NativeLayoutsMatchWindowsHeaders()
    {
        Assert.Equal(64, Marshal.SizeOf<MemoryStatusEx>());
        Assert.Equal(IntPtr.Size == 8 ? 104 : 56, Marshal.SizeOf<PerformanceInformation>());
        Assert.Equal(IntPtr.Size == 8 ? 80 : 44, Marshal.SizeOf<ProcessMemoryCountersEx>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<PerformanceInformation>(nameof(PerformanceInformation.CommitTotal)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.OffsetOf<ProcessMemoryCountersEx>(nameof(ProcessMemoryCountersEx.WorkingSetSize)).ToInt32());
    }

    [Fact]
    public async Task CommitUsesPageSizeRatherThanPagefileOrPhysicalValues()
    {
        var api = new FakeMemoryApi();
        var result = await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(1000UL, result.Value.TotalPhysicalBytes);
        Assert.Equal(400UL, result.Value.AvailablePhysicalBytes);
        Assert.Equal(7UL * 4096, result.Value.CommitTotalBytes);
        Assert.Equal(11UL * 4096, result.Value.CommitLimitBytes);
        Assert.Equal(DataCompleteness.Complete, result.Value.Completeness);
    }

    [Fact]
    public async Task MissingCommitReadKeepsPhysicalDataAndMarksPartial()
    {
        var api = new FakeMemoryApi { Performance = () => throw new Win32Exception(5) };
        var result = await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.CommitTotalBytes);
        Assert.Null(result.Value.CommitLimitBytes);
        Assert.Equal(DataCompleteness.Partial, result.Value.Completeness);
    }

    [Fact]
    public async Task InvalidPhysicalReadDoesNotPublishWrappedUsage()
    {
        var api = new FakeMemoryApi { Physical = () => new() { TotalPhysical = 10, AvailablePhysical = 11 } };
        var result = await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task NativeFailureProducesStructuredError()
    {
        var result = await new WindowsMemoryMonitor(new FakeMemoryApi { Physical = () => throw new Win32Exception(5) })
            .ReadAsync(CancellationToken.None);
        Assert.Equal(ErrorCodes.AccessDenied, result.Error?.Code);
    }

    [Fact]
    public async Task OverflowedCommitNeverProducesPartialWrappedNumbers()
    {
        if (IntPtr.Size != 8) return;
        var api = new FakeMemoryApi { Performance = () => new() { CommitTotal = 1, CommitLimit = nuint.MaxValue, PageSize = 4096 } };
        var result = await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None);
        Assert.Null(result.Value.CommitTotalBytes);
        Assert.Null(result.Value.CommitLimitBytes);
        Assert.Equal(DataCompleteness.Partial, result.Value.Completeness);
    }

    [Fact]
    public async Task ProcessExitAndAccessDenialDoNotDiscardReadableProcesses()
    {
        var api = new FakeMemoryApi
        {
            Ids = [11, 12, 13, 14],
            Process = pid => pid switch
            {
                12 => throw new Win32Exception(5),
                13 => throw new Win32Exception(1168),
                14 => new(pid, null, null, 900, 1500),
                _ => new(pid, "sample.exe", DateTimeOffset.UtcNow, 100, 300)
            }
        };
        var result = await new WindowsProcessMemoryMonitor(api).ReadAsync(CancellationToken.None);
        var data = result.Value;
        Assert.Equal(4, data.EnumeratedCount);
        Assert.Equal(2, data.ReadableCount);
        Assert.Equal(1, data.AccessDeniedCount);
        Assert.Equal(1, data.UnavailableCount);
        Assert.Equal(14U, data.Entries[0].ProcessId);
        Assert.Equal("PID 14", data.Entries[0].Name);
        Assert.False(data.Entries[0].IsIdentityVerified);
        Assert.True(data.Entries[1].IsIdentityVerified);
        Assert.Equal(DataCompleteness.Partial, data.Completeness);
    }

    [Fact]
    public async Task ProcessResultsAreBoundedAndSortedAfterReading()
    {
        var api = new FakeMemoryApi
        {
            Ids = Enumerable.Range(1, 230).Select(x => (uint)x).ToArray(),
            Process = pid => new(pid, "sample.exe", null, pid * 1024UL, pid * 2048UL)
        };
        var data = (await new WindowsProcessMemoryMonitor(api).ReadAsync(CancellationToken.None)).Value;
        Assert.Equal(230, data.ReadableCount);
        Assert.Equal(200, data.Entries.Count);
        Assert.Equal(230U, data.Entries[0].ProcessId);
        Assert.True(data.Entries.Zip(data.Entries.Skip(1)).All(pair => pair.First.WorkingSetBytes >= pair.Second.WorkingSetBytes));
    }

    [Fact]
    public async Task UnsupportedPlatformDoesNotCallNativeFunctions()
    {
        var api = new FakeMemoryApi { IsSupported = false, Physical = () => throw new InvalidOperationException() };
        Assert.False((await new WindowsMemoryMonitor(api).ReadAsync(CancellationToken.None)).IsSuccess);
        Assert.False((await new WindowsProcessMemoryMonitor(api).ReadAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task PreCancelledCollectionsDoNotBegin()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsMemoryMonitor(new FakeMemoryApi()).ReadAsync(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsProcessMemoryMonitor(new FakeMemoryApi()).ReadAsync(source.Token));
    }

    [Fact]
    public void HistoryKeepsOnlyTheNewestBoundedSamples()
    {
        var history = new MemoryHistory(60);
        var time = DateTimeOffset.UtcNow;
        for (var index = 0; index < 140; index++) history.Add(new(time.AddSeconds(index), 100, 40, null, null, DataCompleteness.Partial));
        Assert.Equal(60, history.Points.Count);
        Assert.Equal(time.AddSeconds(80), history.Points[0].TimestampUtc);
        Assert.Equal(time.AddSeconds(139), history.Points[^1].TimestampUtc);
        Assert.Equal(60.0, history.Points[0].UsedPercent);
        history.Clear();
        Assert.Empty(history.Points);
    }

    [Fact]
    public void FormattingUsesBinaryUnitsAndGuardsInvalidDenominators()
    {
        Assert.Equal("—", MemoryFormatting.GiB(null));
        Assert.Null(MemoryFormatting.Percentage(1, 0));
        Assert.Null(MemoryFormatting.Percentage(2, 1));
        Assert.Equal(100.0, MemoryFormatting.Percentage(ulong.MaxValue, ulong.MaxValue));
        Assert.Contains("1", MemoryFormatting.MiB(1UL << 20));
        Assert.EndsWith("MiB", MemoryFormatting.MiB(1UL << 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryHistory(0));
    }
}

internal sealed class FakeMemoryApi : IWindowsMemoryApi
{
    public bool IsSupported { get; init; } = true;
    public Func<MemoryStatusEx> Physical { get; init; } = () => new() { TotalPhysical = 1000, AvailablePhysical = 400, TotalPageFile = 99999 };
    public Func<PerformanceInformation> Performance { get; init; } = () => new() { CommitTotal = 7, CommitLimit = 11, PageSize = 4096 };
    public uint[] Ids { get; init; } = [];
    public Func<uint, NativeProcessSample> Process { get; init; } = pid => new(pid, null, null, 0, 0);
    public MemoryStatusEx ReadPhysical() => Physical();
    public PerformanceInformation ReadPerformance() => Performance();
    public ProcessIdBatch EnumerateProcessIds(CancellationToken ct) => new(Ids, true);
    public NativeProcessSample ReadProcess(uint pid) => Process(pid);
}
