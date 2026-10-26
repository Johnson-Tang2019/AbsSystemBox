// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using SysBox.Abstractions;
using Windows.Devices.Power;
using Windows.System.Power;

namespace SysBox.App.Services;

// Reuses Microsoft's existing open-source C#/WinRT projection; see docs/BATTERY.md.
// No direct device IOCTL, driver, WMI command or battery control operation is used.
public sealed class WindowsBatteryMonitor : IBatteryMonitor
{
    public Task<BatteryReading> ReadAsync(CancellationToken cancellationToken) => Task.Run(()=>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var battery = Battery.AggregateBattery;
            if(battery is null) return new BatteryReading(BatteryChargeState.NotPresent,null,null,null,DateTimeOffset.UtcNow);
            var report = battery.GetReport();
            var state = report.Status switch
            {
                BatteryStatus.Charging => BatteryChargeState.Charging,
                BatteryStatus.Discharging => BatteryChargeState.Discharging,
                BatteryStatus.NotPresent => BatteryChargeState.NotPresent,
                _ => BatteryChargeState.Idle
            };
            return new BatteryReading(state,report.ChargeRateInMilliwatts,report.FullChargeCapacityInMilliwattHours,
                report.RemainingCapacityInMilliwattHours,DateTimeOffset.UtcNow);
        }
        catch(Exception e) when(e is COMException or UnauthorizedAccessException or NotSupportedException)
        { return new BatteryReading(BatteryChargeState.Unavailable,null,null,null,DateTimeOffset.UtcNow,"电池报告不可用。"); }
    },cancellationToken);
}
#if DEBUG
public sealed class DemoBatteryMonitor : IBatteryMonitor
{
    public Task<BatteryReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new BatteryReading(BatteryChargeState.Charging,45000,75000,30000,DateTimeOffset.UtcNow));
    }
}
#endif
