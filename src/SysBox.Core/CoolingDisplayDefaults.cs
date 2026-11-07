// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
namespace SysBox.Core;

public static class CoolingDisplayDefaults
{
    public static CoolingDisplaySettings Select(CoolingDisplaySettings settings, CoolingDevice[] devices, CoolingSensor[] sensors)
    {
        var device = settings.DeviceId;
        if (device is not null)
        {
            if (!devices.Any(d => d.DeviceId == device)) throw new InvalidDataException("保存的水冷屏已不可用，请重新选择。");
        }
        else if (devices.Length == 1) device = devices[0].DeviceId;
        else throw new InvalidDataException("请明确选择唯一的水冷屏。");
        if (settings.ManualValues) return settings with { DeviceId = device };
        return settings with { DeviceId = device,
            TemperatureSensorId = Sensor(settings.TemperatureSensorId, "Core Max", CoolingSensorKind.Temperature, sensors),
            FanSensorId = Sensor(settings.FanSensorId, "CPU Fan", CoolingSensorKind.Fan, sensors) };
    }
    private static string Sensor(string? saved, string label, CoolingSensorKind kind, CoolingSensor[] sensors)
    {
        if (saved is not null)
        {
            if (!sensors.Any(s => s.Id == saved && s.Kind == kind)) throw new InvalidDataException($"保存的 {label} 传感器已不可用，请重新选择。");
            return saved;
        }
        var matches = sensors.Where(s => s.Kind == kind && string.Equals(s.Name.Split('·')[^1].Trim(), label, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"{label} 未找到或存在多个同名传感器，请明确选择。");
        return matches[0].Id;
    }
}
