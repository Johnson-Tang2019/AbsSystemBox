// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class PeripheralStartupTests
{
    [Fact]
    public async Task LaunchAndFirstPageVisitUseTheSameDetection()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);

        var startup = viewModel.LoadIfNeededAsync();
        var pageVisit = viewModel.LoadIfNeededAsync();
        var request = Assert.Single(discovery.Requests);
        Assert.True(viewModel.IsRefreshing);

        var mouse = Mouse("startup");
        request.Complete(mouse);
        await Task.WhenAll(startup, pageVisit);

        Assert.Equal(mouse, Assert.Single(viewModel.Devices));
        Assert.True(viewModel.HasLoaded);
        Assert.False(viewModel.IsRefreshing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASuccessfulSnapshotIsReusedUntilTheUserRefreshes(bool hasDevice)
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var startup = viewModel.LoadIfNeededAsync();
        PeripheralDevice[] initial = hasDevice ? [Mouse("startup")] : [];
        Assert.Single(discovery.Requests).Complete(initial);
        await startup;
        var firstSnapshot = viewModel.Devices;

        var pageVisit = viewModel.LoadIfNeededAsync();
        Assert.Single(discovery.Requests);
        await pageVisit;

        Assert.Same(firstSnapshot, viewModel.Devices);
        Assert.Equal(initial, viewModel.Devices);

        var refresh = viewModel.RefreshAsync();
        Assert.Equal(2, discovery.Requests.Count);
        var updated = Mouse("refresh");
        discovery.Requests[1].Complete(updated);
        await refresh;

        Assert.Equal(updated, Assert.Single(viewModel.Devices));
    }

    [Fact]
    public async Task ThePageAutomaticallyRetriesAStartupFailure()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var startup = viewModel.LoadIfNeededAsync();
        var error = new SysBoxError(ErrorCodes.Unavailable, "无法读取设备。", retryable: true);
        Assert.Single(discovery.Requests).Reply.SetResult(Result<IReadOnlyList<PeripheralDevice>>.Failure(error));
        await startup;
        Assert.Equal(error.Message, viewModel.ErrorMessage);

        var pageVisit = viewModel.LoadIfNeededAsync();
        Assert.Equal(2, discovery.Requests.Count);
        var mouse = Mouse("retry");
        discovery.Requests[1].Complete(mouse);
        await pageVisit;

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(mouse, Assert.Single(viewModel.Devices));
    }

    [Fact]
    public async Task ThePageRechecksAKnownReceiverWithoutMouseReadings()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var startup = viewModel.LoadIfNeededAsync();
        var receiver = Mouse("receiver") with
        {
            Connection = PeripheralConnection.WirelessReceiver,
            Presence = PeripheralPresence.ReceiverOnly,
            ReceiverName = "MCHOSE K7 Ultra"
        };
        Assert.Single(discovery.Requests).Complete(receiver);
        await startup;
        var pageVisit = viewModel.LoadIfNeededAsync();
        Assert.Equal(2, discovery.Requests.Count);
        var online = receiver with
        {
            Presence = PeripheralPresence.Detected,
            MouseReadout = new MousePeripheralReadout(90, "5.28.4.0",
                new MousePerformanceReadout([2800], [2800], 0, 0, 1000, 2, 3, 8))
        };
        discovery.Requests[1].Complete(online);
        await pageVisit;

        await viewModel.LoadIfNeededAsync();
        Assert.Equal(2, discovery.Requests.Count);
        Assert.Equal(online, Assert.Single(viewModel.Devices));
    }

    [Fact]
    public async Task LeavingThePageKeepsTheSnapshotAndDiscardsACancelledRefresh()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var startup = viewModel.LoadIfNeededAsync();
        var mouse = Mouse("startup");
        Assert.Single(discovery.Requests).Complete(mouse);
        await startup;
        viewModel.ShowDevice(mouse);
        var firstSnapshot = viewModel.Devices;

        var refresh = viewModel.RefreshAsync();
        Assert.Equal(2, discovery.Requests.Count);
        var pending = discovery.Requests[1];
        viewModel.Deactivate();

        Assert.True(pending.Token.IsCancellationRequested);
        Assert.True(viewModel.HasLoaded);
        Assert.False(viewModel.IsRefreshing);
        Assert.Null(viewModel.SelectedKind);
        Assert.Same(firstSnapshot, viewModel.Devices);

        var pageReentry = viewModel.LoadIfNeededAsync();
        Assert.Equal(2, discovery.Requests.Count);
        await pageReentry;
        pending.Complete(Mouse("late"));
        await refresh;

        Assert.Same(firstSnapshot, viewModel.Devices);
        Assert.Equal(mouse, Assert.Single(viewModel.Devices));
    }

    [Fact]
    public async Task ConfirmedWriteUpdatesTheSelectedDeviceWithoutStartingAnotherDetection()
    {
        var discovery = new ControlledDiscovery();
        var viewModel = new PeripheralsViewModel(discovery);
        var startup = viewModel.LoadIfNeededAsync();
        var keyboard = Mouse("keyboard") with { Kind = PeripheralKind.Keyboard, KeyboardLighting = new(1, 3, null) };
        Assert.Single(discovery.Requests).Complete(keyboard);
        await startup;
        viewModel.ShowDevice(keyboard);

        var confirmed = keyboard with { KeyboardLighting = new(2, 4, 4) };
        viewModel.UpdateConfirmedDevice(confirmed);

        Assert.Equal(confirmed, viewModel.SelectedDevice);
        Assert.Single(discovery.Requests);
    }

    private static PeripheralDevice Mouse(string id) => new(
        id, PeripheralKind.Mouse, id, null, null, PeripheralModelEvidence.Unknown,
        PeripheralConnection.Usb, PeripheralPresence.Detected, null, null);

    private sealed class ControlledDiscovery : IPeripheralDiscovery
    {
        public List<Detection> Requests { get; } = [];

        public Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken)
        {
            var request = new Detection(cancellationToken);
            Requests.Add(request);
            return request.Reply.Task;
        }
    }

    private sealed record Detection(CancellationToken Token)
    {
        public TaskCompletionSource<Result<IReadOnlyList<PeripheralDevice>>> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(params PeripheralDevice[] devices) =>
            Reply.SetResult(Result<IReadOnlyList<PeripheralDevice>>.Success(devices));
    }
}
