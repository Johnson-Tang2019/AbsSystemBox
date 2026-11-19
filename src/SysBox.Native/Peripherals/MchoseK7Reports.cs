// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class MchoseK7Reports
{
    internal const string BodyId = "3837:4150";
    internal const string ReceiverName = "MCHOSE K7 Ultra";
    internal const int ReportLength = 65;
    private const byte ShortReport = 0x11;
    private const byte LongReport = 0x12;
    private const byte IdentityCommand = 0x06;
    private const byte FirmwareCommand = 0x04;
    private const byte PerformanceCommand = 0x67;

    internal static bool Matches(ushort? vendor, ushort? product, PeripheralConnection connection,
        ushort page, ushort usage, ushort featureLength, string? productName) =>
        connection is PeripheralConnection.Usb or PeripheralConnection.WirelessReceiver &&
        MatchesDescriptor(vendor, product, page, usage, featureLength) &&
        string.Equals(productName, ReceiverName, StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesDescriptor(ushort? vendor, ushort? product, ushort page, ushort usage,
        ushort featureLength) => (vendor, product, page, usage, featureLength) is (0x5253, 0x1020, 0xFF01, 1, ReportLength);

    internal static byte[] IdentityRequest() => CreateRequest(ShortReport, IdentityCommand, 20);
    internal static byte[] FirmwareRequest() => CreateRequest(ShortReport, FirmwareCommand, 20);
    internal static byte[] PerformanceRequest() => CreateRequest(LongReport, PerformanceCommand, 64);

    private static byte[] CreateRequest(byte reportId, byte command, int encodedLength)
    {
        var report = new byte[ReportLength];
        report[0] = reportId;
        report.AsSpan(1, encodedLength).Fill(byte.MaxValue);
        report[1] ^= command;
        return report;
    }

    internal static bool IsEmptyIdentity(ReadOnlySpan<byte> report) =>
        MatchesResponse(report, ShortReport, IdentityCommand) &&
        BinaryPrimitives.ReadUInt32LittleEndian(report[2..]) == uint.MaxValue;

    internal static bool IsEmptyPerformance(ReadOnlySpan<byte> report) =>
        MatchesResponse(report, LongReport, PerformanceCommand) && !report[2..].ContainsAnyExcept(byte.MaxValue);

    internal static MousePeripheralReadout? DecodeIdentity(ReadOnlySpan<byte> report)
    {
        if (!MatchesResponse(report, ShortReport, IdentityCommand))
            return null;
        var vendor = (ushort)~BinaryPrimitives.ReadUInt16LittleEndian(report[2..]);
        var product = (ushort)~BinaryPrimitives.ReadUInt16LittleEndian(report[4..]);
        var link = (byte)~report[10];
        if ((vendor, product) != (0x3837, 0x4150) || (link & 0x0F) != 0x09)
            return null;

        var battery = (byte)~report[11];
        return new MousePeripheralReadout(battery <= 100 ? battery : null);
    }

    internal static string? DecodeFirmware(ReadOnlySpan<byte> report)
    {
        if (!MatchesResponse(report, ShortReport, FirmwareCommand))
            return null;
        var length = (byte)~report[2];
        if (length is < 7 or > 18)
            return null;

        Span<byte> text = stackalloc byte[length];
        for (var index = 0; index < length; index++)
        {
            var value = (byte)~report[index + 3];
            if (value != '.' && value is not (>= (byte)'0' and <= (byte)'9'))
                return null;
            text[index] = value;
        }

        var version = Encoding.ASCII.GetString(text);
        return Version.TryParse(version, out var parsed) && parsed.Revision >= 0 ? version : null;
    }

    internal static MousePerformanceReadout? DecodePerformance(ReadOnlySpan<byte> report)
    {
        if (!MatchesResponse(report, LongReport, PerformanceCommand))
            return null;
        Span<byte> payload = stackalloc byte[ReportLength - 2];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)~report[index + 2];

        var count = payload[16];
        var active = payload[1] & 0x0F;
        if (count is < 1 or > 6 || active >= count)
            return null;

        var horizontal = DecodeDpiStages(payload[4..], count);
        if (horizontal is null)
            return null;
        return new MousePerformanceReadout(horizontal, DecodeDpiStages(payload[51..], count), active,
            payload[0] <= 2 ? payload[0] : null, PollingRate(payload[2] >> 4),
            (payload[17] & 0x03) switch { 0 => 0.7m, 1 => 1m, 2 => 2m, _ => null },
            payload[19], payload[18]);
    }

    private static IReadOnlyList<int>? DecodeDpiStages(ReadOnlySpan<byte> data, int count)
    {
        var stages = new int[count];
        for (var index = 0; index < stages.Length; index++)
        {
            var dpi = BinaryPrimitives.ReadUInt16LittleEndian(data[(index * 2)..]);
            if (dpi is < 50 or > 42000)
                return null;
            stages[index] = dpi;
        }
        return Array.AsReadOnly(stages);
    }

    private static int? PollingRate(int code) => code switch
    {
        0 => 125,
        1 => 500,
        2 => 1000,
        3 => 2000,
        4 => 4000,
        5 => 8000,
        _ => null
    };

    private static bool MatchesResponse(ReadOnlySpan<byte> report, byte reportId, byte command) =>
        report.Length == ReportLength && report[0] == reportId && report[1] == (byte)~command;
}
