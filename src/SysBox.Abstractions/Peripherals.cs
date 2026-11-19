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
    CatalogMatched
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
    ushort? ProductId);

public interface IPeripheralDiscovery
{
    Task<Result<IReadOnlyList<PeripheralDevice>>> DiscoverAsync(CancellationToken cancellationToken);
}
