// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;
namespace SysBox.UnitTests;

public sealed class BatteryTests
{
    private static BatteryReading Reading(BatteryChargeState state=BatteryChargeState.Charging,int? rate=45000,int? full=75000,int? remaining=30000) => new(state,rate,full,remaining,DateTimeOffset.UtcNow);
    [Fact] public void NetWattsAndHoursUseActualFullCapacity()
    {
        var estimate=BatteryEstimate.From(Reading());
        Assert.True(estimate.IsCharging);Assert.Equal(45,estimate.NetChargeWatts);Assert.Equal(TimeSpan.FromHours(1),estimate.TimeToFull);
    }
    [Theory] [InlineData(BatteryChargeState.NotPresent)] [InlineData(BatteryChargeState.Unavailable)] [InlineData(BatteryChargeState.Discharging)] [InlineData(BatteryChargeState.Idle)]
    public void HiddenUnlessCharging(BatteryChargeState state)
    {
        var estimate=BatteryEstimate.From(Reading(state));
        Assert.False(estimate.IsCharging);Assert.Null(estimate.NetChargeWatts);Assert.Null(estimate.TimeToFull);
    }
    [Theory] [InlineData(null)] [InlineData(0)] [InlineData(-45000)] [InlineData(int.MinValue)]
    public void UnknownAndInvalidRateNeverBecomePositivePower(int? rate)
    {
        var estimate=BatteryEstimate.From(Reading(rate:rate));
        Assert.True(estimate.IsCharging);Assert.Null(estimate.NetChargeWatts);Assert.Null(estimate.TimeToFull);
    }
    [Theory] [InlineData(null,30000)] [InlineData(0,0)] [InlineData(-1,0)] [InlineData(75000,null)] [InlineData(75000,-1)] [InlineData(75000,75001)]
    public void BadCapacityCannotProduceEstimate(int? full,int? remaining)
    {
        var estimate=BatteryEstimate.From(Reading(full:full,remaining:remaining));
        Assert.Equal(45,estimate.NetChargeWatts);Assert.Null(estimate.TimeToFull);
    }
    [Fact] public void NearFullAndImplausiblyLongEstimate()
    {
        Assert.Equal(TimeSpan.Zero,BatteryEstimate.From(Reading(remaining:75000)).TimeToFull);
        Assert.Null(BatteryEstimate.From(Reading(rate:1)).TimeToFull);
    }
    [Fact] public async Task PresentationClearsOnUnplugAndIgnoresLateRead()
    {
        var monitor=new TestMonitor();var model=new BatteryViewModel(monitor);
        var task=model.RefreshAsync();monitor.Pending.SetResult(Reading());await task;
        Assert.True(model.IsCharging);Assert.Contains("45.0",model.PowerText);Assert.Contains("60",model.TimeText);
        monitor.Pending=new();task=model.RefreshAsync();monitor.Pending.SetResult(Reading(BatteryChargeState.Discharging));await task;
        Assert.False(model.IsCharging);
        monitor.Pending=new();task=model.RefreshAsync();model.Deactivate();monitor.Pending.SetResult(Reading());await task;
        Assert.False(model.IsCharging);Assert.Null(model.Reading);
    }
    private sealed class TestMonitor : IBatteryMonitor
    {
        public TaskCompletionSource<BatteryReading> Pending=new();
        public Task<BatteryReading> ReadAsync(CancellationToken cancellationToken)=>Pending.Task;
    }
}
