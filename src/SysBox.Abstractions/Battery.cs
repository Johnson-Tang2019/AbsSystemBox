// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum BatteryChargeState { Unavailable, NotPresent, Idle, Discharging, Charging }
public sealed record BatteryReading(BatteryChargeState State, int? ChargeRateMilliwatts, int? FullChargeCapacityMilliwattHours,
    int? RemainingCapacityMilliwattHours, DateTimeOffset TimestampUtc, string? Error = null);
public interface IBatteryMonitor
{
    Task<BatteryReading> ReadAsync(CancellationToken cancellationToken);
}
