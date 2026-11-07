// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record CoolingDevice(string PortName, string DeviceId)
{
    public string DisplayName => $"金河田水冷显示屏 · {PortName}";
}

public enum CoolingSensorKind { Temperature, Fan }
public sealed record CoolingSensor(string Id, string Name, CoolingSensorKind Kind, double Value);
public sealed record CoolingSensorSnapshot(DateTimeOffset SampledAt, CoolingSensor[] Sensors, string Message);

public interface ICoolingSensorSource
{
    Task<CoolingSensorSnapshot> ReadAsync(CancellationToken token);
}

public interface ICoolingDisplayConnection : IDisposable
{
    Task WriteAsync(int temperature, int fanRpm, CancellationToken token);
}

public interface ICoolingDisplayTransport
{
    Task<CoolingDevice[]> DiscoverAsync(CancellationToken token);
    Task<ICoolingDisplayConnection> ConnectAsync(CoolingDevice device, CancellationToken token);
}

public sealed record CoolingDisplaySettings
{
    public string? DeviceId { get; init; }
    public string? TemperatureSensorId { get; init; }
    public string? FanSensorId { get; init; }
    public bool AutoStart { get; init; }
    public bool ManualValues { get; init; }
    public int ManualTemperature { get; init; } = 25;
    public int ManualFanRpm { get; init; } = 1000;
}
