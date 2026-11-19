// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class MchoseK7Reports
{
    internal const string BodyId = "3837:4150";
    internal const string ReceiverName = "MCHOSE K7 Ultra";
    internal const int ReportLength = 65;
    private const string WritableFirmware = "5.28.4.0";
    private const int MinDpi = 50;
    private const int MaxDpi = 42000;
    private const int StageCount = 6;
    private const int DpiByteCount = StageCount * sizeof(ushort);
    private const int HorizontalDpiOffset = 4;
    private const int VerticalDpiOffset = 51;
    private const int ProfileOffset = 0;
    private const int WirelessStageOffset = 1;
    private const int UsbStageOffset = 2;
    private const int DeviceStateOffset = 3;
    private const int EnabledStagesOffset = 16;
    private const byte ShortReport = 0x11;
    private const byte LongReport = 0x12;
    private const byte IdentityCommand = 0x06;
    private const byte FirmwareCommand = 0x04;
    private const byte WriteDpiCommand = 0x40;
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

    internal static MousePerformanceReadout? DecodePerformance(ReadOnlySpan<byte> report, string? firmware = null)
    {
        if (DecodePerformancePayload(report) is not { } payload)
            return null;

        var count = payload[EnabledStagesOffset];
        var active = payload[WirelessStageOffset] & 0x0F;
        if (count is < 1 or > StageCount || active >= count)
            return null;

        var horizontal = DecodeDpiStages(payload.AsSpan(HorizontalDpiOffset), count);
        if (horizontal is null)
            return null;
        return new MousePerformanceReadout(horizontal, DecodeDpiStages(payload.AsSpan(VerticalDpiOffset), count), active,
            payload[ProfileOffset] <= 2 ? payload[ProfileOffset] : null, PollingRate(payload[UsbStageOffset] >> 4),
            (payload[17] & 0x03) switch { 0 => 0.7m, 1 => 1m, 2 => 2m, _ => null },
            payload[19], payload[18], CanEditDpi(payload, firmware) ? GetEditRevision(payload) : null);
    }

    internal static DpiEdit? PrepareDpiEdit(ReadOnlySpan<byte> report, string? firmware, int stage, int dpi)
    {
        if (DecodePerformancePayload(report) is not { } before || !CanEditDpi(before, firmware) ||
            stage < 0 || stage >= before[EnabledStagesOffset] || !ValidDpi(dpi) || dpi % 50 != 0)
            return null;

        var after = before.ToArray();
        var offset = stage * sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(after.AsSpan(HorizontalDpiOffset + offset), (ushort)dpi);
        BinaryPrimitives.WriteUInt16LittleEndian(after.AsSpan(VerticalDpiOffset + offset), (ushort)dpi);
        return new DpiEdit(before, after);
    }

    private static byte[]? DecodePerformancePayload(ReadOnlySpan<byte> report)
    {
        if (!MatchesResponse(report, LongReport, PerformanceCommand))
            return null;
        var payload = new byte[ReportLength - 2];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)~report[index + 2];
        return payload;
    }

    private static bool CanEditDpi(ReadOnlySpan<byte> payload, string? firmware)
    {
        var count = payload[EnabledStagesOffset];
        var horizontal = payload.Slice(HorizontalDpiOffset, DpiByteCount);
        var vertical = payload.Slice(VerticalDpiOffset, DpiByteCount);
        // The short DPI command mirrors the whole horizontal table onto the vertical axis.
        return firmware == WritableFirmware && payload[ProfileOffset] <= 2 &&
            count is >= 1 and <= StageCount && (payload[WirelessStageOffset] & 0x0F) < count &&
            (payload[UsbStageOffset] & 0x0F) < count && horizontal.SequenceEqual(vertical) &&
            DecodeDpiStages(horizontal, StageCount) is { } values && values.All(dpi => dpi % 50 == 0);
    }

    private static string GetEditRevision(ReadOnlySpan<byte> payload)
    {
        Span<byte> normalized = stackalloc byte[ReportLength - 2];
        payload.CopyTo(normalized);
        normalized[DeviceStateOffset] = 0;
        return Convert.ToHexString(SHA256.HashData(normalized));
    }

    private static byte[] CreateDpiRequest(ReadOnlySpan<byte> config, byte deviceState)
    {
        var report = CreateRequest(ShortReport, WriteDpiCommand, 20);
        report[2] = (byte)~(config[UsbStageOffset] & 0x0F);
        report[3] = (byte)~(config[WirelessStageOffset] & 0x0F);
        report[4] = (byte)~deviceState;
        for (var index = 0; index < DpiByteCount; index++)
            report[5 + index] = (byte)~config[HorizontalDpiOffset + index];
        // The protocol calls this sum. It is the count of enabled stages.
        report[17] = (byte)~config[EnabledStagesOffset];
        return report;
    }

    internal sealed class DpiEdit(byte[] before, byte[] after)
    {
        internal byte[] ApplyRequest { get; } = CreateDpiRequest(after, after[DeviceStateOffset]);

        internal bool IsApplied(ReadOnlySpan<byte> report) => MatchesConfiguration(report, after);

        internal bool IsOriginal(ReadOnlySpan<byte> report) => MatchesConfiguration(report, before);

        internal byte[]? CreateRestoreRequest(ReadOnlySpan<byte> report)
        {
            if (DecodePerformancePayload(report) is not { } received)
                return null;
            ReadOnlySpan<byte> current = received;
            ReadOnlySpan<byte> original = before;
            // Keep a newly selected profile, active index, or unrelated configuration intact.
            if (!current[..DeviceStateOffset].SequenceEqual(original[..DeviceStateOffset]) ||
                !current[EnabledStagesOffset..VerticalDpiOffset].SequenceEqual(original[EnabledStagesOffset..VerticalDpiOffset]))
                return null;
            return CreateDpiRequest(original, current[DeviceStateOffset]);
        }

        private static bool MatchesConfiguration(ReadOnlySpan<byte> report, ReadOnlySpan<byte> expected)
        {
            if (DecodePerformancePayload(report) is not { } received)
                return false;
            ReadOnlySpan<byte> actual = received;
            // Byte 3 is maintained by the device when it applies DPI updates.
            return actual[..DeviceStateOffset].SequenceEqual(expected[..DeviceStateOffset]) &&
                actual[(DeviceStateOffset + 1)..].SequenceEqual(expected[(DeviceStateOffset + 1)..]);
        }
    }

    private static IReadOnlyList<int>? DecodeDpiStages(ReadOnlySpan<byte> data, int count)
    {
        var stages = new int[count];
        for (var index = 0; index < stages.Length; index++)
        {
            var dpi = BinaryPrimitives.ReadUInt16LittleEndian(data[(index * 2)..]);
            if (!ValidDpi(dpi))
                return null;
            stages[index] = dpi;
        }
        return Array.AsReadOnly(stages);
    }

    private static bool ValidDpi(int dpi) => dpi is >= MinDpi and <= MaxDpi;

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
