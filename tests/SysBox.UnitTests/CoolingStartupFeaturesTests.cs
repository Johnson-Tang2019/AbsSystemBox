// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;
namespace SysBox.UnitTests;
public sealed class CoolingStartupFeaturesTests
{
    private static readonly CoolingDevice[] Devices = [new("COM3", "screen")];
    private static readonly CoolingSensor[] Sensors = [new("core", "CPU · Core Max", CoolingSensorKind.Temperature, 44), new("fan", "Board · CPU Fan", CoolingSensorKind.Fan, 1200), new("gpu", "GPU · GPU Core", CoolingSensorKind.Temperature, 60)];
    [Fact]
    public void UniqueExactDefaultsSelectCpuAndPersistedIdsRemainAuthoritative()
    {
        var selected = CoolingDisplayDefaults.Select(new(), Devices, Sensors);
        Assert.False(selected.AutoStart);
        Assert.Equal("screen", selected.DeviceId);
        Assert.Equal("core", selected.TemperatureSensorId);
        Assert.Equal("fan", selected.FanSensorId);
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(selected, Devices, Sensors.Where(s => s.Id != "core").ToArray()));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(selected, [new("COM4", "other")], Sensors));
    }
    [Fact]
    public void MissingAmbiguousOrWrongKindCpuLabelsNeverFallBackToGpu()
    {
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(new(), Devices, Sensors.Where(s => s.Id != "core").ToArray()));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(new(), Devices, [..Sensors, new("other", "Other CPU · Core Max", CoolingSensorKind.Temperature, 50)]));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(new(), Devices, [Sensors[0] with { Kind = CoolingSensorKind.Fan }, Sensors[1]]));
        Assert.Throws<InvalidDataException>(() => CoolingDisplayDefaults.Select(new(), [..Devices, new("COM4", "second")], Sensors));
    }
    [Fact]
    public async Task DefaultDisabledDoesNotTouchHardwareAndMissingServicePreventsAutoConnection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CoolingStartup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoolingDisplayPreferenceStore(directory);
            var transport = new Transport();
            var vm = new CoolingDisplayViewModel(transport, new Source(true), store);
            await vm.StartAtApplicationLaunchAsync();
            Assert.Equal(0, transport.Discoveries);
            store.Save(new() { AutoStart = true });
            vm = new(transport, new Source(true), store);
            await vm.StartAtApplicationLaunchAsync();
            Assert.False(vm.IsRunning);
            Assert.Equal(0, transport.Connections);
            Assert.Contains("服务不可用", vm.Status);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task AutoStartPersistsStableDefaultsAndPageDepartureKeepsConnectionAliveUntilExplicitStop()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CoolingStartup-" + Guid.NewGuid().ToString("N"));
        var store = new CoolingDisplayPreferenceStore(directory);
        store.Save(new() { AutoStart = true });
        var transport = new Transport();
        var vm = new CoolingDisplayViewModel(transport, new Source(false), store);
        try
        {
            await vm.StartAtApplicationLaunchAsync();
            Assert.True(vm.IsRunning);
            vm.CancelPageDetection();
            await transport.Connection.Written.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(vm.IsRunning);
            Assert.False(transport.Connection.Disposed);
            Assert.Equal("core", store.Load().TemperatureSensorId);
            Assert.Equal("fan", store.Load().FanSensorId);
            vm.Settings = new() { AutoStart = false };
            vm.Load();
            Assert.False(vm.Settings.AutoStart);
            vm.Deactivate();
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(transport.Connection.Disposed);
        }
        finally { vm.Deactivate(); await vm.Completion; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Source(bool fail) : ICoolingSensorSource
    {
        public Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token) => fail ? throw new IOException("服务不可用") : Task.FromResult(new CoolingSensorSnapshot(DateTimeOffset.UtcNow, Sensors, "服务采集"));
    }
    private sealed class Transport : ICoolingDisplayTransport
    {
        public int Discoveries, Connections;
        public readonly Connection Connection = new();
        public Task<CoolingDevice[]> DiscoverAsync(CancellationToken token) { Discoveries++; return Task.FromResult(Devices); }
        public Task<ICoolingDisplayConnection> ConnectAsync(CoolingDevice device, CancellationToken token) { Connections++; return Task.FromResult<ICoolingDisplayConnection>(Connection); }
    }
    private sealed class Connection : ICoolingDisplayConnection
    {
        public readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Task WriteAsync(int temperature, int fanRpm, CancellationToken token) { Written.TrySetResult(); return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
}
