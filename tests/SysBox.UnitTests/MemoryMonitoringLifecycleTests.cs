// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class MemoryMonitoringLifecycleTests
{
    private static Result<MemorySnapshot> Sample() => Result<MemorySnapshot>.Success(
        new(DateTimeOffset.UtcNow, 100, 40, 70, 150, DataCompleteness.Complete));
    private static Result<ProcessMemorySnapshot> Processes() => Result<ProcessMemorySnapshot>.Success(
        new(DateTimeOffset.UtcNow, [], 0, 0, 0, 0, 0, 0, true, DataCompleteness.Complete));

    [Fact]
    public async Task StopCancelsTheTimerAndRestartOwnsExactlyOneLoop()
    {
        var clock = new ControlledDelay();
        var calls = 0;
        var vm = new MemoryViewModel(new MemoryClient(_ => { calls++; return Task.FromResult(Sample()); }),
            new ProcessClient(_ => Task.FromResult(Processes())), clock);
        vm.Start();
        vm.Start();
        Assert.Equal(1, calls);
        Assert.True(vm.IsMonitoring);
        var loop = vm.MonitoringTask;
        clock.Tick();
        await Eventually(() => calls == 2 && !vm.IsBusy);
        Assert.Equal(2, vm.History.Count);
        vm.Stop();
        await loop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsMonitoring);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, calls);
        vm.Start();
        Assert.Equal(3, calls);
        var restarted = vm.MonitoringTask;
        vm.Deactivate();
        await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(vm.History);
        Assert.Empty(vm.Processes);
        Assert.Null(vm.Snapshot);
    }

    [Fact]
    public async Task DeactivateDiscardsLateReadAndCancelsBothCollectors()
    {
        var pending = new TaskCompletionSource<Result<MemorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken memoryToken = default;
        CancellationToken processToken = default;
        var vm = new MemoryViewModel(new MemoryClient(ct => { memoryToken = ct; return pending.Task; }),
            new ProcessClient(ct => { processToken = ct; return Task.FromResult(Processes()); }), new ControlledDelay());
        var read = vm.RefreshAsync();
        vm.Deactivate();
        pending.SetResult(Sample());
        await read;
        Assert.True(memoryToken.IsCancellationRequested);
        Assert.True(processToken.IsCancellationRequested);
        Assert.Null(vm.Snapshot);
        Assert.Empty(vm.History);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FailedProcessCollectorDoesNotHidePhysicalMemory()
    {
        var vm = new MemoryViewModel(new MemoryClient(_ => Task.FromResult(Sample())),
            new ProcessClient(_ => throw new IOException("private path")), new ControlledDelay());
        await vm.RefreshAsync();
        Assert.NotNull(vm.Snapshot);
        Assert.Single(vm.History);
        Assert.Empty(vm.Processes);
        Assert.DoesNotContain("private path", vm.ProcessStatus);
    }

    [Fact]
    public async Task CancelledLateSuccessDoesNotBecomeANewReading()
    {
        var pending = new TaskCompletionSource<Result<MemorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MemoryViewModel(new MemoryClient(_ => pending.Task),
            new ProcessClient(_ => Task.FromResult(Processes())), new ControlledDelay());
        var read = vm.RefreshAsync();
        vm.Cancel();
        pending.SetResult(Sample());
        await read;
        Assert.Empty(vm.History);
        Assert.Null(vm.Snapshot);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ManualRefreshCannotOverlapAnActiveRead()
    {
        var pending = new TaskCompletionSource<Result<MemorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = new MemoryViewModel(new MemoryClient(_ => { calls++; return pending.Task; }),
            new ProcessClient(_ => Task.FromResult(Processes())), new ControlledDelay());
        var read = vm.RefreshAsync();
        await vm.RefreshAsync();
        Assert.Equal(1, calls);
        vm.Cancel();
        pending.SetResult(Sample());
        await read;
    }

    [Fact]
    public async Task FailedReadRetainsTheLastTimestampWithoutGrowingHistory()
    {
        var calls = 0;
        var vm = new MemoryViewModel(new MemoryClient(_ => Task.FromResult(++calls == 1
                ? Sample() : Result<MemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "not available")))),
            new ProcessClient(_ => Task.FromResult(Processes())), new ControlledDelay());
        await vm.RefreshAsync();
        var previous = vm.Snapshot;
        await vm.RefreshAsync();
        Assert.Same(previous, vm.Snapshot);
        Assert.Single(vm.History);
        Assert.True(vm.HasError);
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }

    [Fact]
    public async Task StableRowsReplaceIdentityWithoutRebuildingTheCollection()
    {
        var calls = 0;
        var started = DateTimeOffset.UtcNow;
        var vm = new MemoryViewModel(new MemoryClient(_ => Task.FromResult(Sample())),
            new ProcessClient(_ => Task.FromResult(Result<ProcessMemorySnapshot>.Success(new(DateTimeOffset.UtcNow,
                [new(42, ++calls == 1 ? "old" : "new", null, started.AddMinutes(calls), (ulong)calls, 4)],
                1, 1, 0, 0, 0, 0, true, DataCompleteness.Complete)))), new ControlledDelay());
        var collection = vm.Processes;
        await vm.RefreshAsync();
        var row = vm.Processes.Single();
        var identity = row.Entry!.StartTimeUtc;
        await vm.RefreshAsync();
        Assert.Same(collection, vm.Processes);
        Assert.Same(row, vm.Processes.Single());
        Assert.Equal("new", row.Name);
        Assert.NotEqual(identity, row.Entry!.StartTimeUtc);
        vm.Deactivate();
        Assert.Empty(vm.Processes);
        Assert.Null(row.Entry);
        Assert.Equal("—", row.Name);
    }

    [Fact]
    public void EqualProcessReadingsDoNotTriggerRedundantBindingUpdates()
    {
        var entry = new ProcessMemoryEntry(42, "app", "app.exe", DateTimeOffset.UtcNow, 100, 200);
        var row = new ProcessMemoryRow();
        row.Update(entry);
        var notifications = 0;
        row.PropertyChanged += (_, _) => notifications++;
        row.Update(entry with { });
        Assert.Equal(0, notifications);
        row.Update(entry with { WorkingSetBytes = 300 });
        Assert.Equal(1, notifications);
    }

    private sealed class ControlledDelay : ISamplingDelay
    {
        private readonly Queue<TaskCompletionSource<bool>> waits = new();
        public Task WaitAsync(TimeSpan interval, CancellationToken ct)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), interval);
            var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            waits.Enqueue(next);
            return next.Task.WaitAsync(ct);
        }
        public void Tick() => waits.Dequeue().SetResult(true);
    }
    private sealed class MemoryClient(Func<CancellationToken, Task<Result<MemorySnapshot>>> read) : IMemoryMonitor
    {
        public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken ct) => read(ct);
    }
    private sealed class ProcessClient(Func<CancellationToken, Task<Result<ProcessMemorySnapshot>>> read) : IProcessMemoryMonitor
    {
        public Task<Result<ProcessMemorySnapshot>> ReadAsync(CancellationToken ct) => read(ct);
    }
}
