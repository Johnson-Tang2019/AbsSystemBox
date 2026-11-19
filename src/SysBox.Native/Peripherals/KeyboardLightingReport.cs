// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class KeyboardLightingReport
{
    private const int PayloadLength = 128;
    private const int CustomFlag = 9;
    private const int Effect = 10;
    private const int EffectTable = 58;
    private static ReadOnlySpan<byte> ReadHeader => [0x06, 0x84, 0, 0, 0x01, 0, 0x80, 0];
    private static ReadOnlySpan<byte> WriteHeader => [0x06, 0x04, 0, 0, 0x01, 0, 0x80, 0];

    // F87 Pro 010B profile and configuration layout documented by meursault03/aula-f87pro-linux.
    // Source: https://github.com/meursault03/aula-f87pro-linux/blob/825a91a46d9b4573c7bfebdeb90a607b7638b709/src/lib.rs
    private static readonly IReadOnlyList<int> Effects = Array.AsReadOnly<int>(
        [0, 1, 2, 3, 4, 5, 7, 8, 10, 11, 12, 13, 15, 16, 17]);

    internal static byte[] CreateRequest()
    {
        var report = new byte[KeyboardIdentityReport.ReportLength];
        ReadHeader.CopyTo(report);
        return report;
    }

    internal static byte[]? ReadPayload(ReadOnlySpan<byte> report)
    {
        if (report.Length != KeyboardIdentityReport.ReportLength || !report.StartsWith(ReadHeader))
            return null;
        var payload = report.Slice(ReadHeader.Length, PayloadLength);
        if (payload[56] != 0xFF || payload[57] != 0xFF)
            return null;

        return payload.ToArray();
    }

    internal static KeyboardLightingReadout? Decode(ReadOnlySpan<byte> report) =>
        ReadPayload(report) is { } payload ? Describe(payload) : null;

    internal static KeyboardLightingReadout Describe(ReadOnlySpan<byte> payload)
    {
        var effect = payload[Effect];
        if (!IsEditable(payload))
            return new(effect, null, null);

        int? brightness = null;
        int? speed = null;
        int? palette = null;
        if (effect != 0)
        {
            var offset = EffectOffset(effect);
            var settings = payload[offset + 1];
            brightness = payload[offset] <= 4 ? payload[offset] : null;
            var speedValue = settings >> 4;
            var paletteValue = settings & 0x0F;
            speed = SupportsSpeed(effect) && speedValue <= 4 ? speedValue : null;
            palette = SupportsPalette(effect) && paletteValue <= 7 ? paletteValue : null;
        }

        return new(effect, brightness, speed, Convert.ToHexString(SHA256.HashData(payload)),
            EditableEffects: Effects, PaletteIndex: palette);
    }

    internal static byte[]? ApplyChange(ReadOnlySpan<byte> original, KeyboardLightingChange change)
    {
        if (!IsEditable(original) || !Effects.Contains(change.EffectId) ||
            change.BrightnessLevel is < 0 or > 4 || change.SpeedLevel is < 0 or > 4 ||
            change.PaletteIndex is < 0 or > 7 ||
            (change.EffectId == 0 && change.BrightnessLevel is not null) ||
            (change.SpeedLevel is not null && !SupportsSpeed(change.EffectId)) ||
            (change.PaletteIndex is not null && !SupportsPalette(change.EffectId)))
            return null;

        var desired = original.ToArray();
        desired[Effect] = (byte)change.EffectId;
        if (change.EffectId == 0)
            return desired;

        var offset = EffectOffset(change.EffectId);
        if (change.BrightnessLevel is int brightness)
            desired[offset] = (byte)brightness;
        if (change.SpeedLevel is int speed)
            desired[offset + 1] = (byte)((desired[offset + 1] & 0x0F) | (speed << 4));
        if (change.PaletteIndex is int palette)
            desired[offset + 1] = (byte)((desired[offset + 1] & 0xF0) | palette);
        return desired;
    }

    internal static byte[] CreateWriteRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
            throw new ArgumentException("Invalid keyboard configuration size.", nameof(payload));

        var report = new byte[KeyboardIdentityReport.ReportLength];
        WriteHeader.CopyTo(report);
        payload.CopyTo(report.AsSpan(WriteHeader.Length));
        return report;
    }

    internal static bool CanRestore(ReadOnlySpan<byte> original, ReadOnlySpan<byte> desired, ReadOnlySpan<byte> observed)
    {
        if (original.Length != PayloadLength || desired.Length != PayloadLength || observed.Length != PayloadLength)
            return false;
        for (var offset = 0; offset < PayloadLength; offset++)
        {
            var requestedBits = original[offset] ^ desired[offset];
            if (((original[offset] ^ observed[offset]) & ~requestedBits) != 0)
                return false;
        }
        return true;
    }

    private static bool IsEditable(ReadOnlySpan<byte> payload) => payload.Length == PayloadLength &&
        payload[CustomFlag] == 0 && Effects.Contains(payload[Effect]);

    private static int EffectOffset(int effect) => EffectTable + 2 * (effect - 1);
    private static bool SupportsSpeed(int effect) => effect > 1;
    private static bool SupportsPalette(int effect) => effect is not (0 or 3 or 15 or 16 or 17);
}
