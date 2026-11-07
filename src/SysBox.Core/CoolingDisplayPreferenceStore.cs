// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;

namespace SysBox.Core;

public sealed class CoolingDisplayPreferenceStore(string directory)
{
    private readonly string path = Path.Combine(directory, "cooling-display.json");
    public CoolingDisplaySettings Load()
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("水冷显示设置文件过大。");
        var settings = JsonSerializer.Deserialize<CoolingDisplaySettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("水冷显示设置为空。");
        Validate(settings);
        return settings;
    }

    public void Save(CoolingDisplaySettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, path, true);
    }

    private static void Validate(CoolingDisplaySettings settings)
    {
        _ = GoldenFiledDisplayProtocol.Encode(settings.ManualTemperature, settings.ManualFanRpm);
        if (settings.DeviceId?.Length > 512 || settings.TemperatureSensorId?.Length > 256 || settings.FanSensorId?.Length > 256)
            throw new InvalidDataException("水冷显示设置的标识过长。");
    }
}
