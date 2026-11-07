// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;
namespace SysBox.UnitTests;

public sealed class CoolingDisplayFeaturesTests
{
    [Theory]
    [InlineData(0, 0, "7400020202")]
    [InlineData(42, 1500, "74002807DE")]
    [InlineData(255, 65535, "7400FDFDFD")]
    public void FrameUsesLiteralHeaderAndXorPayload(int temperature, int fan, string expected) => Assert.Equal(expected, Convert.ToHexString(GoldenFiledDisplayProtocol.Encode(temperature, fan)));
    [Theory]
    [InlineData(-1, 0)] [InlineData(256, 0)] [InlineData(0, -1)] [InlineData(0, 65536)]
    public void FrameRejectsOverflow(int temperature, int fan) => Assert.Throws<ArgumentOutOfRangeException>(() => GoldenFiledDisplayProtocol.Encode(temperature, fan));
    [Fact]
    public void SnapshotRequiresBothSelectedSensorsAndFreshFiniteInRangeValues()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new CoolingDisplaySettings { TemperatureSensorId = "t", FanSensorId = "f" };
        var valid = new CoolingSensorSnapshot(now, [new("t", "CPU", CoolingSensorKind.Temperature, 42.8), new("f", "Fan", CoolingSensorKind.Fan, 1500.9)], "");
        Assert.Equal((42, 1500), CoolingDisplayViewModel.Resolve(valid, settings, now));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayViewModel.Resolve(valid with { SampledAt = now.AddSeconds(-6) }, settings, now));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayViewModel.Resolve(valid with { Sensors = [valid.Sensors[0]] }, settings, now));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1, 256 })
            Assert.Throws<InvalidDataException>(() => CoolingDisplayViewModel.Resolve(valid with { Sensors = [valid.Sensors[0] with { Value = value }, valid.Sensors[1]] }, settings, now));
    }
    [Fact]
    public async Task LeavingPageDiscardsLateDiscoveryAndPreventsAutomaticConnection()
    {
        var transport = new DelayedTransport();
        var vm = new CoolingDisplayViewModel(transport, new Source(), new CoolingDisplayPreferenceStore(Path.GetTempPath()));
        var refresh = vm.RefreshAsync();
        vm.Deactivate();
        transport.Pending.SetResult([new("COM3", "device")]);
        await refresh;
        Assert.Empty(vm.Devices);
        Assert.False(vm.IsRunning);
        Assert.Equal(0, transport.Connections);
    }
    [Fact]
    public async Task StopDuringConnectionDisposesLateConnectionWithoutWriting()
    {
        var transport = new DelayedTransport();
        transport.Pending.SetResult([new("COM3", "device")]);
        var vm = new CoolingDisplayViewModel(transport, new Source(), new CoolingDisplayPreferenceStore(Path.GetTempPath()));
        await vm.RefreshAsync();
        vm.Settings = new() { DeviceId = "device", ManualValues = true };
        vm.Start();
        Assert.True(vm.IsRunning);
        vm.Deactivate();
        var connection = new Connection();
        transport.Connected.SetResult(connection);
        await vm.Completion;
        Assert.True(connection.Disposed);
        Assert.Equal(0, connection.Writes);
    }
    [Fact]
    public void SavedSettingsDoNotContainAutomaticStartAndRoundTripSelections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SysBoxCooling-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoolingDisplayPreferenceStore(directory);
            var settings = new CoolingDisplaySettings { DeviceId = "d", TemperatureSensorId = "t", FanSensorId = "f", ManualValues = true, ManualTemperature = 32, ManualFanRpm = 1234 };
            store.Save(settings);
            Assert.Equal(settings, store.Load());
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(settings with { ManualTemperature = 256 }));
            Assert.Equal(settings, store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task SensorDetectionFailureKeepsDiscoveredDisplayAvailableForManualUse()
    {
        var transport = new DelayedTransport();
        transport.Pending.SetResult([new("COM3", "device")]);
        var vm = new CoolingDisplayViewModel(transport, new ThrowingSource(), new CoolingDisplayPreferenceStore(Path.GetTempPath()));
        await vm.RefreshAsync();
        Assert.Equal("device", Assert.Single(vm.Devices).DeviceId);
        Assert.Empty(vm.Temperatures);
        Assert.Empty(vm.Fans);
        Assert.Contains("传感器不可用", vm.SensorStatus);
        Assert.Equal(0, transport.Connections);
    }
    [Fact]
    public async Task AutomaticLoopStopsWhenSelectedSensorDisappearsWithoutSendingZero()
    {
        var transport = new DelayedTransport();
        transport.Pending.SetResult([new("COM3", "device")]);
        var connection = new RecordingConnection();
        transport.Connected.SetResult(connection);
        var source = new DisappearingSource();
        var vm = new CoolingDisplayViewModel(transport, source, new CoolingDisplayPreferenceStore(Path.GetTempPath()));
        await vm.RefreshAsync();
        vm.Settings = new() { DeviceId = "device", TemperatureSensorId = "t", FanSensorId = "f" };
        vm.Start();
        try
        {
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal((43, 1400), Assert.Single(connection.Values));
            Assert.Equal(3, source.Reads);
            Assert.False(vm.IsRunning);
            Assert.True(connection.Disposed);
            Assert.Contains("所选温度或风扇传感器不可用", vm.Status);
        }
        finally { vm.Deactivate(); await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }
    [Fact]
    public async Task ExplicitManualModeSendsWithoutAnySensorReadDuringSynchronization()
    {
        var transport = new DelayedTransport();
        transport.Pending.SetResult([new("COM3", "device")]);
        var connection = new RecordingConnection();
        transport.Connected.SetResult(connection);
        var source = new ThrowingSource();
        var vm = new CoolingDisplayViewModel(transport, source, new CoolingDisplayPreferenceStore(Path.GetTempPath()));
        await vm.RefreshAsync();
        vm.Settings = new() { DeviceId = "device", ManualValues = true, ManualTemperature = 31, ManualFanRpm = 1234 };
        vm.Start();
        try
        {
            await connection.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Deactivate();
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal((31, 1234), Assert.Single(connection.Values));
            Assert.Equal(1, source.Reads);
            Assert.True(connection.Disposed);
        }
        finally { vm.Deactivate(); await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
    }
    private sealed class ThrowingSource : ICoolingSensorSource
    {
        public int Reads;
        public Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token)
        { Reads++; throw new IOException("采集组件不可用"); }
    }
    private sealed class DisappearingSource : ICoolingSensorSource
    {
        public int Reads;
        public Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token)
        {
            Reads++;
            CoolingSensor[] values = Reads < 3 ? [new("t", "CPU", CoolingSensorKind.Temperature, 43.9), new("f", "Fan", CoolingSensorKind.Fan, 1400.8)] : [new("t", "CPU", CoolingSensorKind.Temperature, 44)];
            return Task.FromResult(new CoolingSensorSnapshot(DateTimeOffset.UtcNow, values, "独立采集"));
        }
    }
    private sealed class RecordingConnection : ICoolingDisplayConnection
    {
        public readonly List<(int Temperature, int FanRpm)> Values = [];
        public readonly TaskCompletionSource FirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public void Dispose() => Disposed = true;
        public Task WriteAsync(int temperature, int fanRpm, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Values.Add((temperature, fanRpm)); FirstWrite.TrySetResult(); return Task.CompletedTask;
        }
    }
    private sealed class Source : ICoolingSensorSource
    {
        public Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new CoolingSensorSnapshot(DateTimeOffset.UtcNow, [], "鏃犱紶鎰熷櫒"));
    }
    private sealed class DelayedTransport : ICoolingDisplayTransport
    {
        public readonly TaskCompletionSource<CoolingDevice[]> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ICoolingDisplayConnection> Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Connections;
        public Task<CoolingDevice[]> DiscoverAsync(CancellationToken token) => Pending.Task;
        public Task<ICoolingDisplayConnection> ConnectAsync(CoolingDevice device, CancellationToken token) { Connections++; return Connected.Task; }
    }
    private sealed class Connection : ICoolingDisplayConnection
    {
        public bool Disposed;
        public int Writes;
        public void Dispose() => Disposed = true;
        public Task WriteAsync(int temperature, int fanRpm, CancellationToken token) { Writes++; return Task.CompletedTask; }
    }
}
