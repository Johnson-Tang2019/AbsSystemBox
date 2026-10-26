// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
namespace SysBox.Core;

public sealed record BatteryEstimate(bool IsCharging, double? NetChargeWatts, TimeSpan? TimeToFull)
{
    public static BatteryEstimate From(BatteryReading reading)
    {
        if(reading.State != BatteryChargeState.Charging) return new(false,null,null);
        var watts = reading.ChargeRateMilliwatts is > 0 ? reading.ChargeRateMilliwatts / 1000d : null;
        TimeSpan? duration = null;
        if(reading.FullChargeCapacityMilliwattHours is > 0 and var full && reading.RemainingCapacityMilliwattHours is >= 0 and var remaining && remaining <= full && watts is > 0)
        {
            var hours = (full - remaining) / (watts.Value * 1000d);
            if(hours <= 168) duration = TimeSpan.FromHours(hours);
        }
        return new(true,watts,duration);
    }
}
