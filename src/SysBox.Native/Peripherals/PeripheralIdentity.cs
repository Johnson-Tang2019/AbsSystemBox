// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class PeripheralIdentity
{
    private static readonly HashSet<string> GenericProductWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "2.4G", "2.4GHz", "Bluetooth", "Composite", "Device", "Generic", "Gaming", "Gamepad", "Controller", "HID",
        "Input", "Joystick", "Keyboard", "Keypad", "Mouse", "USB", "Wired", "Wireless",
        "MCHOSE", "MAICONG", "AULA", "FLYDIGI", "迈从", "狼蛛", "飞智", "鼠标", "键盘", "手柄",
        "游戏手柄", "输入设备", "蓝牙设备", "复合设备", "无线鼠标", "无线键盘"
    };

    internal static IReadOnlyList<PeripheralDevice> Identify(IReadOnlyList<PeripheralInterface> interfaces)
    {
        var devices = new List<PeripheralDevice>();
        foreach (var container in interfaces.GroupBy(
            item => string.IsNullOrWhiteSpace(item.GroupId) ? item.Id : item.GroupId,
            StringComparer.OrdinalIgnoreCase))
        {
            var candidates = container.Select(Classify).ToArray();
            var groups = candidates
                .Where(candidate => candidate.Kind.HasValue)
                .GroupBy(candidate => candidate.Kind.GetValueOrDefault())
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var candidate in candidates.Where(IsAdditionalVendorInterface))
            {
                var matchingKinds = groups
                    .Where(group => group.Value.Any(known => MatchesInterface(candidate, known)))
                    .Select(group => group.Key)
                    .Take(2)
                    .ToArray();
                if (matchingKinds.Length == 1)
                    groups[matchingKinds[0]].Add(candidate);
            }

            foreach (var group in groups)
                devices.Add(Describe(container.Key, group.Key, group.Value));
        }

        return devices
            .OrderBy(device => device.Kind)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static Candidate Classify(PeripheralInterface item)
    {
        if (item is { VendorId: 0x3837, ProductId: 0x1012, UsagePage: 0xFF00, Usage: 1, Connection: PeripheralConnection.Usb })
            return new(item, PeripheralKind.ChargingDock, "迈从", new CatalogEntry(PeripheralKind.ChargingDock, "迈从", "MagDock"));

        var hidKind = (item.UsagePage, item.Usage) switch
        {
            (0x01, 0x02) => PeripheralKind.Mouse,
            (0x01, 0x06 or 0x07) => PeripheralKind.Keyboard,
            (0x01, 0x04 or 0x05) => PeripheralKind.Gamepad,
            _ => (PeripheralKind?)null
        };
        var catalog = item.Connection != PeripheralConnection.Bluetooth &&
            (hidKind.HasValue || item.UsagePage is >= 0xff00)
            ? FindCatalogEntry(item.VendorId, item.ProductId)
            : null;
        var brand = RecognizeBrand(item.Manufacturer) ?? RecognizeBrand(item.ProductName) ?? catalog?.Brand;
        return new(item, catalog?.Kind ?? hidKind, brand, catalog);
    }

    private static bool IsAdditionalVendorInterface(Candidate candidate) =>
        !candidate.Kind.HasValue &&
        candidate.Brand is not null &&
        candidate.Interface.UsagePage is >= 0xff00 &&
        candidate.Interface.VendorId.HasValue &&
        candidate.Interface.ProductId.HasValue;

    private static bool MatchesInterface(Candidate extra, Candidate known) =>
        extra.Interface.VendorId == known.Interface.VendorId &&
        extra.Interface.ProductId == known.Interface.ProductId &&
        (known.Brand is null || extra.Brand == known.Brand);

    private static PeripheralDevice Describe(string container, PeripheralKind kind, List<Candidate> candidates)
    {
        var receiver = candidates.Any(candidate => candidate.Catalog?.IsReceiver == true ||
            candidate.Interface.Connection == PeripheralConnection.WirelessReceiver ||
            (candidate.Interface.Connection != PeripheralConnection.Bluetooth &&
                IsReceiverName(candidate.Interface.ProductName)));
        var connections = candidates
            .Select(candidate => candidate.Catalog?.UsbMayBeReceiver == true &&
                candidate.Interface.Connection == PeripheralConnection.Usb
                    ? PeripheralConnection.Unknown
                    : candidate.Interface.Connection)
            .Where(connection => connection != PeripheralConnection.Unknown)
            .Distinct()
            .Take(2)
            .ToArray();
        var connection = receiver ? PeripheralConnection.WirelessReceiver :
            connections.Length == 1 ? connections[0] : PeripheralConnection.Unknown;

        // A receiver's metadata belongs to the receiver. Its product string cannot identify a paired device.
        var systemModels = candidates.Select(candidate => ModelFromProductName(candidate.Interface.ProductName))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var catalogModel = SingleValue(candidates.Select(candidate => candidate.Catalog?.Model));
        var keyboardIdentity = kind == PeripheralKind.Keyboard && !receiver
            ? SingleValue(candidates.Select(candidate => candidate.Interface.HardwareIdentityCode)) : null;
        var mouse = kind == PeripheralKind.Mouse && receiver
            ? SingleReadout(candidates.Select(candidate => candidate.Interface.MouseReadout)) : null;
        var keyboardModel = KeyboardIdentityReport.Model(keyboardIdentity);
        var keyboardLighting = keyboardModel is not null
            ? SingleReadout(candidates.Where(candidate => candidate.Interface.HardwareIdentityCode == keyboardIdentity)
                .Select(candidate => candidate.Interface.KeyboardLighting)) : null;
        var keyboardPerKey = keyboardModel is not null
            ? SingleReadout(candidates.Where(candidate => candidate.Interface.HardwareIdentityCode == keyboardIdentity)
                .Select(candidate => candidate.Interface.KeyboardPerKey)) : null;
        var magDock = kind == PeripheralKind.ChargingDock
            ? SingleReadout(candidates.Select(candidate => candidate.Interface.MagDockLighting)) : null;
        var hardwareModel = mouse is not null ? "K7 Ultra" : keyboardModel;
        var model = hardwareModel ?? (receiver ? null : systemModels.Length switch
        {
            1 => systemModels[0],
            0 => catalogModel,
            _ => null
        });
        var modelEvidence = model is null ? PeripheralModelEvidence.Unknown :
            hardwareModel is not null ? PeripheralModelEvidence.HardwareReported :
            systemModels.Length == 1 ? PeripheralModelEvidence.SystemReported : PeripheralModelEvidence.CatalogMatched;

        var manufacturer = mouse is not null ? "迈从" : keyboardModel is not null ? "狼蛛" :
            SingleValue(candidates.Select(candidate => candidate.Brand)) ??
            SingleValue(candidates.Select(candidate => ManufacturerFromSystem(candidate.Interface.Manufacturer)));
        var category = kind switch
        {
            PeripheralKind.Mouse => "鼠标",
            PeripheralKind.Keyboard => "键盘",
            PeripheralKind.ChargingDock => "充电底座",
            _ => "手柄"
        };
        var label = receiver ? category + "接收器" : category;
        var name = modelEvidence == PeripheralModelEvidence.SystemReported ? model! :
            JoinLabel(manufacturer, model ?? label);
        var (vendor, product) = SelectHardwareIds(candidates);
        var id = DeviceId(container, kind);
        var presence = receiver && mouse is null ? PeripheralPresence.ReceiverOnly : PeripheralPresence.Detected;
        var receiverName = receiver ? SingleValue(candidates.Select(candidate => candidate.Interface.ProductName)) : null;
        return new(id, kind, name, manufacturer, model, modelEvidence, connection, presence, vendor, product,
            mouse is not null ? MchoseK7Reports.BodyId : keyboardIdentity, receiverName, mouse, keyboardLighting,
            keyboardPerKey, magDock);
    }

    internal static string DeviceId(string container, PeripheralKind kind)
    {
        var keyBytes = Encoding.UTF8.GetBytes(container.ToUpperInvariant());
        return $"{Convert.ToHexString(SHA256.HashData(keyBytes))}:{kind}";
    }

    private static (ushort? Vendor, ushort? Product) SelectHardwareIds(List<Candidate> candidates)
    {
        var known = candidates.Where(candidate => candidate.Catalog is not null).ToArray();
        var identifiable = known.Length == 0 ? candidates.AsEnumerable() : known;
        var pairs = identifiable
            .Select(candidate => (candidate.Interface.VendorId, candidate.Interface.ProductId))
            .Where(pair => pair.VendorId.HasValue && pair.ProductId.HasValue)
            .Distinct()
            .Take(2)
            .ToArray();
        return pairs.Length == 1 ? pairs[0] : (null, null);
    }

    private static string? ModelFromProductName(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text) || IsReceiverName(text) ||
            text.StartsWith("HID-", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("HID ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("符合 HID", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("XBOX 360 FOR WINDOWS", StringComparison.OrdinalIgnoreCase))
            return null;

        var words = text.Split([' ', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.Any(word => !GenericProductWords.Contains(word)) ? text : null;
    }

    private static bool IsReceiverName(string? text) =>
        text?.Contains("接收器", StringComparison.Ordinal) == true ||
        HasWord(text, "receiver") || HasWord(text, "dongle") || HasWord(text, "transceiver");

    private static string? ManufacturerFromSystem(string? value)
    {
        var text = value?.Trim();
        // PnP uses parenthesized names for generic INF providers, including localized placeholder names.
        return text is null || (text.StartsWith('(') && text.EndsWith(')')) ? null : text;
    }

    private static string? RecognizeBrand(string? text)
    {
        if (text?.Contains("迈从", StringComparison.Ordinal) == true ||
            HasWord(text, "MCHOSE") || HasWord(text, "MAICONG"))
            return "迈从";
        if (text?.Contains("狼蛛", StringComparison.Ordinal) == true || HasWord(text, "AULA"))
            return "狼蛛";
        if (text?.Contains("飞智", StringComparison.Ordinal) == true || HasWord(text, "FLYDIGI"))
            return "飞智";
        return null;
    }

    private static bool HasWord(string? text, string word)
    {
        if (text is null)
            return false;
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            var end = index + word.Length;
            if ((index == 0 || !char.IsAsciiLetterOrDigit(text[index - 1])) &&
                (end == text.Length || !char.IsAsciiLetterOrDigit(text[end])))
                return true;
            start = end;
        }
    }

    private static string? SingleValue(IEnumerable<string?> values)
    {
        var candidates = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static T? SingleReadout<T>(IEnumerable<T?> values) where T : class
    {
        var candidates = values.OfType<T>().Distinct().Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string JoinLabel(string? brand, string label) =>
        string.IsNullOrWhiteSpace(brand) ? label : $"{brand} {label}";

    // Only metadata labels come from this table. These IDs cannot authorize protocol access.
    private static CatalogEntry? FindCatalogEntry(ushort? vendor, ushort? product) => (vendor, product) switch
    {
        // alexfrih/mchose-linux, zzxzzk115/mchose-control-linux, aipno/mchose-linux and OpenMHub.
        (0x5253, 0x1020 or 0x1021) or (0x3837, 0x1001 or 0x4245) => new(PeripheralKind.Mouse, "迈从", IsReceiver: true),
        (0x3837, 0x418c) => new(PeripheralKind.Mouse, "迈从", "G3 V2"),
        // Aula-Manager documents IDs reused by many models. 258a:010c also appears under other brands.
        (0x258a, 0x010c) => new(PeripheralKind.Keyboard),
        (0x3554, 0xfa09) => new(PeripheralKind.Keyboard, IsReceiver: true),
        // openflydigi and flydigi-space-station-mac document vendor collections for these controller IDs.
        (0x37d7, 0x2401 or 0x2501) => new(PeripheralKind.Gamepad, "飞智", UsbMayBeReceiver: true),
        _ => null
    };

    private readonly record struct Candidate(
        PeripheralInterface Interface, PeripheralKind? Kind, string? Brand, CatalogEntry? Catalog);

    private sealed record CatalogEntry(
        PeripheralKind Kind, string? Brand = null, string? Model = null,
        bool IsReceiver = false, bool UsbMayBeReceiver = false);
}
