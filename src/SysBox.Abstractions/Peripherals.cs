// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum PeripheralKind
{
    Mouse,
    Keyboard,
    Gamepad
}

public enum PeripheralConnection
{
    Unknown,
    Usb,
    WirelessReceiver,
    Bluetooth
}

public enum PeripheralPresence
{
    Detected,
    ReceiverOnly
}

public enum PeripheralModelEvidence
{
    Unknown,
    SystemReported,
    CatalogMatched,
    HardwareReported
}

public sealed record PeripheralDevice(
    string Id,
    PeripheralKind Kind,
    string Name,
    string? Manufacturer,
    string? Model,
    PeripheralModelEvidence ModelEvidence,
    PeripheralConnection Connection,
    PeripheralPresence Presence,
    ushort? VendorId,
    ushort? ProductId,
    string? HardwareIdentityCode = null,
    string? ReceiverName = null,
    MousePeripheralReadout? MouseReadout = null,
    KeyboardLightingReadout? KeyboardLighting = null);

public sealed record MousePeripheralReadout(
    int? BatteryPercent, string? FirmwareVersion = null, MousePerformanceReadout? Performance = null);

public sealed record MousePerformanceReadout(
    IReadOnlyList<int> DpiStages,
    IReadOnlyList<int>? VerticalDpiStages,
    int ActiveDpiStage,
    int? ActiveProfile,
    int? PollingRateHz,
    decimal? LiftOffDistanceMm,
    int? SleepMinutes,
    int? DebounceMilliseconds,
    string? EditRevision = null);

public sealed record KeyboardLightingReadout(int EffectId, int? BrightnessLevel, int? SpeedLevel,
    string? EditRevision = null, IReadOnlyList<int>? EditableEffects = null, int? PaletteIndex = null);

public sealed record KeyboardLightingChange(int EffectId, int? BrightnessLevel = null,
    int? SpeedLevel = null, int? PaletteIndex = null);

public interface IPeripheralDiscovery
{
    Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken);
}

public interface IPeripheralConfiguration
{
    Task<Result<MousePerformanceReadout>> SetMouseDpiAsync(string deviceId, string expectedRevision,
        int stage, int dpi, CancellationToken cancellationToken);
    Task<Result<KeyboardLightingReadout>> SetKeyboardLightingAsync(string deviceId, string expectedRevision,
        KeyboardLightingChange change, CancellationToken cancellationToken);
}
