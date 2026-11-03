// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
namespace SysBox.Core;

public static class BatteryHealth
{
    public static string Describe(BatteryReading reading)
    {
        if (reading.State == BatteryChargeState.NotPresent)
            return "电池：无";
        if (reading.State == BatteryChargeState.Unavailable)
            return "电池健康度：暂不可用";
        if (reading.DesignCapacityMilliwattHours is not > 0 || reading.FullChargeCapacityMilliwattHours is not > 0)
            return "电池健康度：硬件未提供容量数据";
        return $"电池健康度：{100.0 * reading.FullChargeCapacityMilliwattHours.Value / reading.DesignCapacityMilliwattHours.Value:F1}%";
    }
}
