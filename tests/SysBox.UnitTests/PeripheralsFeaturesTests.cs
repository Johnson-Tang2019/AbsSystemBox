// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class PeripheralsFeaturesTests
{
    [Fact]
    public async Task OverviewOpensTheExactSelectedDeviceInItsCategory()
    {
        var mouse = Device("mouse", PeripheralKind.Mouse);
        var keyboard = Device("keyboard", PeripheralKind.Keyboard);
        var controller = Device("controller", PeripheralKind.Gamepad);
        var secondController = Device("controller-2", PeripheralKind.Gamepad);
        var viewModel = new PeripheralsViewModel(new Discovery(Snapshot(mouse, keyboard, controller, secondController)));
        await viewModel.RefreshAsync();

        Assert.Null(viewModel.SelectedKind);
        Assert.Equal(4, viewModel.Devices.Count);
        viewModel.ShowDevice(secondController);
        Assert.Equal(PeripheralKind.Gamepad, viewModel.SelectedKind);
        Assert.Equal(secondController, viewModel.SelectedDevice);
        Assert.Equal(new[] { controller, secondController }, viewModel.DevicesInSelectedKind);

        viewModel.ShowKind(PeripheralKind.Keyboard);
        Assert.Equal(keyboard, viewModel.SelectedDevice);
        viewModel.ShowOverview();
        Assert.Null(viewModel.SelectedDevice);
        viewModel.ShowKind(PeripheralKind.Gamepad);
        Assert.Equal(secondController, viewModel.SelectedDevice);
    }

    [Fact]
    public async Task RefreshKeepsSelectionMissingUntilTheSameDeviceReturns()
    {
        var selected = Device("mouse-1", PeripheralKind.Mouse);
        var other = Device("mouse-2", PeripheralKind.Mouse);
        var returning = selected with { Connection = PeripheralConnection.Bluetooth };
        var viewModel = new PeripheralsViewModel(new Discovery(
            Snapshot(selected, other), Snapshot(other), Snapshot(other, returning)));

        await viewModel.RefreshAsync();
        viewModel.ShowKind(PeripheralKind.Mouse);
        Assert.Null(viewModel.SelectedDevice);
        viewModel.ShowDevice(selected);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsSelectedDeviceMissing);
        Assert.Null(viewModel.SelectedDevice);
        viewModel.ShowDevice(selected);
        Assert.Null(viewModel.SelectedDevice);
        Assert.Equal(other, Assert.Single(viewModel.DevicesInSelectedKind));
        await viewModel.RefreshAsync();
        Assert.False(viewModel.IsSelectedDeviceMissing);
        Assert.Equal(returning, viewModel.SelectedDevice);
    }

    [Fact]
    public async Task EnumerationFailureClearsTheSnapshotAndAllowsRetry()
    {
        var keyboard = Device("keyboard", PeripheralKind.Keyboard);
        var failure = Result<IReadOnlyList<PeripheralDevice>>.Failure(
            new SysBoxError(ErrorCodes.Unavailable, "无法读取设备。", retryable: true));
        var viewModel = new PeripheralsViewModel(new Discovery(Snapshot(keyboard), failure, Snapshot(keyboard)));
        await viewModel.RefreshAsync();
        viewModel.ShowDevice(keyboard);
        await viewModel.RefreshAsync();

        Assert.Equal(failure.Error!.Message, viewModel.ErrorMessage);
        Assert.True(viewModel.HasLoaded);
        Assert.Empty(viewModel.Devices);
        Assert.Null(viewModel.SelectedDevice);
        Assert.False(viewModel.IsSelectedDeviceMissing);
        await viewModel.RefreshAsync();
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(keyboard, viewModel.SelectedDevice);
    }

    [Fact]
    public async Task DeactivateCancelsAndDiscardsAResponseFromThePreviousVisit()
    {
        var discovery = new DelayedDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var oldRefresh = viewModel.RefreshAsync();
        Assert.True(viewModel.IsRefreshing);
        await viewModel.RefreshAsync();
        var old = Assert.Single(discovery.Requests);
        viewModel.Deactivate();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(viewModel.HasLoaded);
        var newRefresh = viewModel.RefreshAsync();
        var current = discovery.Requests[1];
        var gamepad = Device("current", PeripheralKind.Gamepad);
        current.Response.SetResult(Snapshot(gamepad));
        await newRefresh;
        old.Response.SetResult(Snapshot(Device("old", PeripheralKind.Mouse)));
        await oldRefresh;

        Assert.Equal(gamepad, Assert.Single(viewModel.Devices));
        Assert.False(viewModel.IsRefreshing);
    }

    private static PeripheralDevice Device(string id, PeripheralKind kind) =>
        new(id, kind, id, null, null, PeripheralModelEvidence.Unknown,
            PeripheralConnection.Unknown, PeripheralPresence.Detected, null, null);

    private static Result<IReadOnlyList<PeripheralDevice>> Snapshot(params PeripheralDevice[] devices) =>
        Result<IReadOnlyList<PeripheralDevice>>.Success(devices);

    private sealed class Discovery(params Result<IReadOnlyList<PeripheralDevice>>[] snapshots) : IPeripheralDiscovery
    {
        private readonly Queue<Result<IReadOnlyList<PeripheralDevice>>> events = new(snapshots);

        public Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(events.Dequeue());
    }

    private sealed class DelayedDiscovery : IPeripheralDiscovery
    {
        public List<(CancellationToken Token, TaskCompletionSource<Result<IReadOnlyList<PeripheralDevice>>> Response)>
            Requests { get; } = [];

        public Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<Result<IReadOnlyList<PeripheralDevice>>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((cancellationToken, response));
            return response.Task;
        }
    }
}
