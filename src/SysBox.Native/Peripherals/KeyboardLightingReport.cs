// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class KeyboardLightingReport
{
    private static ReadOnlySpan<byte> Header => [0x06, 0x84, 0, 0, 0x01, 0, 0x80, 0];

    internal static byte[] CreateRequest()
    {
        var report = new byte[KeyboardIdentityReport.ReportLength];
        Header.CopyTo(report);
        return report;
    }

    internal static KeyboardLightingReadout? Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length != KeyboardIdentityReport.ReportLength || !report.StartsWith(Header))
            return null;
        var payload = report.Slice(Header.Length, 128);
        if (payload[56] != 0xFF || payload[57] != 0xFF)
            return null;

        return new KeyboardLightingReadout(payload[10], null, null);
    }
}
