// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class KeyboardIdentityReport
{
    internal const ushort ReportLength = 520;
    private static ReadOnlySpan<byte> Header => [0x06, 0x82, 0x01, 0, 0x01, 0, 0x06, 0];

    // The transport shape and six-byte identity query are documented in AULA Studio's multi-model research.
    internal static bool Matches(ushort? vendor, ushort? product, PeripheralConnection connection,
        ushort page, ushort usage, ushort featureLength, string? manufacturer, string? productName) =>
        MatchesDescriptor(vendor, product, page, usage, featureLength) && connection == PeripheralConnection.Usb &&
        string.Equals(manufacturer, "BY Tech", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(productName, "Gaming Keyboard", StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesDescriptor(ushort? vendor, ushort? product, ushort page, ushort usage, ushort featureLength) =>
        (vendor, product, page, usage, featureLength) is (0x258A, 0x010C, 0xFF00, 1, ReportLength);

    internal static byte[] CreateRequest()
    {
        var report = new byte[ReportLength];
        Header.CopyTo(report);
        return report;
    }

    internal static string? Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength || !report.StartsWith(Header))
            return null;
        var identity = report.Slice(Header.Length, 6);
        if (!identity.ContainsAnyExcept((byte)0) || !identity.ContainsAnyExcept(byte.MaxValue))
            return null;
        return Convert.ToHexString(identity);
    }

    internal static string? Model(string? identity) => identity == "03000000010B" ? "F87 Pro" : null;
}
