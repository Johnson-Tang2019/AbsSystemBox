// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class CoolingReconnectTests
{
    [Theory]
    [InlineData("startup")]
    [InlineData("stream")]
    [InlineData("device")]
    [InlineData("stale")]
    [InlineData("write")]
    public async Task RecoversAndContinuesUsingSavedIdentities(string fault)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CoolingReconnect-" + Guid.NewGuid().ToString("N"));
        var store = new CoolingDisplayPreferenceStore(directory);
        store.Save(new() { AutoStart = true, DeviceId = "screen", TemperatureSensorId = "core", FanSensorId = "fan" });
        var source = new Source(fault);
        var transport = new Transport(fault);
        var vm = new CoolingDisplayViewModel(transport, source, store);
        try
        {
            await vm.StartAtApplicationLaunchAsync();
            Assert.True(vm.IsRunning);
            await transport.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(22));
            Assert.True(vm.IsRunning);
            Assert.Equal("core", store.Load().TemperatureSensorId);
            Assert.Equal("fan", store.Load().FanSensorId);
            vm.Deactivate();
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(transport.Opened, transport.Disposed);
        }
        finally
        {
            vm.Deactivate();
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopCancelsRetryWithoutOpeningDevice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CoolingReconnect-" + Guid.NewGuid().ToString("N"));
        var store = new CoolingDisplayPreferenceStore(directory);
        store.Save(new() { AutoStart = true });
        var transport = new Transport("none");
        var vm = new CoolingDisplayViewModel(transport, new Source("none"), store);
        try
        {
            await vm.StartAtApplicationLaunchAsync();
            await transport.Missing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.IsRunning);
            vm.Deactivate();
            await vm.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, transport.Opened);
            Assert.False(vm.IsRunning);
        }
        finally
        {
            vm.Deactivate();
            await vm.Completion;
            Directory.Delete(directory, true);
        }
    }

    private sealed class Source(string fault) : ICoolingSensorSource
    {
        private int reads;
        public Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var count = Interlocked.Increment(ref reads);
            if (fault == "startup" && count == 1) throw new IOException("服务尚未启动");
            if (fault == "stream" && count == 3) throw new EndOfStreamException();
            var stamp = DateTimeOffset.UtcNow;
            if (fault == "stale" && count == 2) stamp = stamp.AddMinutes(-1);
            return Task.FromResult(new CoolingSensorSnapshot(stamp,
                [new("core", "CPU · Core Max", CoolingSensorKind.Temperature, 43),
                 new("fan", "Board · CPU Fan", CoolingSensorKind.Fan, 1200)], "采集"));
        }
    }

    private sealed class Transport(string fault) : ICoolingDisplayTransport
    {
        public readonly TaskCompletionSource Recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Missing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Opened, Disposed;
        private int discoveries;
        public Task<CoolingDevice[]> DiscoverAsync(CancellationToken token)
        {
            var count = Interlocked.Increment(ref discoveries);
            if (fault == "none" || (fault == "device" && count == 1))
            {
                Missing.TrySetResult();
                return Task.FromResult<CoolingDevice[]>([]);
            }
            return Task.FromResult<CoolingDevice[]>([new(count == 1 ? "COM3" : "COM4", "screen")]);
        }
        public Task<ICoolingDisplayConnection> ConnectAsync(CoolingDevice device, CancellationToken token)
        {
            var attempt = ++Opened;
            return Task.FromResult<ICoolingDisplayConnection>(new Connection(this, attempt, fault));
        }
        private sealed class Connection(Transport owner, int attempt, string fault) : ICoolingDisplayConnection
        {
            public void Dispose() => owner.Disposed++;
            public Task WriteAsync(int temperature, int fanRpm, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal((43, 1200), (temperature, fanRpm));
                if (fault == "write" && attempt == 1) throw new IOException("串口断开");
                if ((fault is "startup" or "device") || attempt > 1) owner.Recovered.TrySetResult();
                return Task.CompletedTask;
            }
        }
    }
}
