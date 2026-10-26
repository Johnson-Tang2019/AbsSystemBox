// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class DashboardLifecycleTests
{
    private static Result<MemorySnapshot> Sample() => Result<MemorySnapshot>.Success(new(
        DateTimeOffset.UtcNow, 16UL << 30, 6UL << 30, null, null, DataCompleteness.Partial));

    [Fact]
    public async Task UnavailableStatusDoesNotBecomeAZeroReading()
    {
        var vm = new DashboardViewModel(new Client(_ => Task.FromResult(
            Result<MemorySnapshot>.Failure(new(ErrorCodes.NotImplemented, "Not available.")))));
        await vm.RefreshAsync();
        Assert.False(vm.IsBusy);
        Assert.True(vm.HasError);
        Assert.Null(vm.Snapshot);
        Assert.Equal("尚未接入", vm.MemorySummary);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelClearsLoadingWithoutReportingAnError()
    {
        var vm = new DashboardViewModel(new Client(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Sample();
        }));
        var refresh = vm.RefreshAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await refresh;
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasError);
        Assert.Null(vm.Snapshot);
        Assert.Equal("已取消读取。", vm.Status);
    }

    [Fact]
    public async Task LeavingThePageRejectsAnUncooperativeLateResult()
    {
        var pending = new TaskCompletionSource<Result<MemorySnapshot>>();
        CancellationToken captured = default;
        var vm = new DashboardViewModel(new Client(ct => { captured = ct; return pending.Task; }));
        var refresh = vm.RefreshAsync();
        vm.Deactivate();
        Assert.True(captured.IsCancellationRequested);
        var status = vm.Status;
        pending.SetResult(Sample());
        await refresh;
        Assert.False(vm.IsBusy);
        Assert.Null(vm.Snapshot);
        Assert.False(vm.HasError);
        Assert.Equal(status, vm.Status);
    }

    [Fact]
    public async Task OldRequestCannotOverwriteNewerPageState()
    {
        var first = new TaskCompletionSource<Result<MemorySnapshot>>();
        var second = new TaskCompletionSource<Result<MemorySnapshot>>();
        var count = 0;
        var vm = new DashboardViewModel(new Client(_ => ++count == 1 ? first.Task : second.Task));
        var old = vm.RefreshAsync();
        vm.Deactivate();
        var current = vm.RefreshAsync();
        first.SetResult(Sample());
        await old;
        Assert.True(vm.IsBusy);
        Assert.Null(vm.Snapshot);
        second.SetResult(Result<MemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Unavailable.")));
        await current;
        Assert.False(vm.IsBusy);
        Assert.True(vm.HasError);
        Assert.Null(vm.Snapshot);
    }

    [Fact]
    public async Task CancelledUncooperativeClientCannotPublishData()
    {
        var pending = new TaskCompletionSource<Result<MemorySnapshot>>();
        var vm = new DashboardViewModel(new Client(_ => pending.Task));
        var refresh = vm.RefreshAsync();
        vm.Cancel();
        pending.SetResult(Sample());
        await refresh;
        Assert.Null(vm.Snapshot);
        Assert.Equal("已取消读取。", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UnexpectedErrorsDoNotLeakPrivateDetailsToTheUI()
    {
        var vm = new DashboardViewModel(new Client(_ => throw new IOException(@"C:\private\credentials")));
        await vm.RefreshAsync();
        Assert.True(vm.HasError);
        Assert.DoesNotContain("credentials", vm.ErrorMessage!);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RetryReplacesFailureStateWithData()
    {
        var calls = 0;
        var vm = new DashboardViewModel(new Client(_ => Task.FromResult(++calls == 1
            ? Result<MemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Unavailable."))
            : Sample())));
        await vm.RefreshAsync();
        Assert.True(vm.HasError);
        await vm.RefreshAsync();
        Assert.False(vm.HasError);
        Assert.NotNull(vm.Snapshot);
        Assert.Contains("6.0 / 16.0", vm.MemorySummary);
    }

    private sealed class Client(Func<CancellationToken, Task<Result<MemorySnapshot>>> read) : IDashboardClient
    {
        public bool IsDemo => false;
        public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken ct) => read(ct);
    }
}
