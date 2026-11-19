// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using SysBox.Abstractions;

namespace SysBox.Native;

internal static class KeyboardPerKeyRgbReport
{
    private const int PlaneLength = 126;
    internal const int PayloadLength = 384;
    private const int ShortReportLength = 8 + PayloadLength;
    private static ReadOnlySpan<byte> ReadHeader => [0x06, 0x86, 0, 0, 0x01, 0, 0x80, 0x01];
    private static ReadOnlySpan<byte> CustomMode => [1, 21, 32];

    // The 010B OEM resource defines the lighting indices for this physical layout.
    // https://github.com/Dahuum/aula-studio/blob/f27dd540b10c7d327d7dfe016147682c31d4fe8d/data/models.json#L2258
    private static readonly (string Key, int Index)[] LedIndices =
    [
        ("Esc", 0), ("F1", 12), ("F2", 18), ("F3", 24), ("F4", 30), ("F5", 36), ("F6", 42),
        ("F7", 48), ("F8", 54), ("F9", 60), ("F10", 66), ("F11", 72), ("F12", 78),
        ("PrintScreen", 84), ("ScrollLock", 90), ("Pause", 96),
        ("`", 1), ("1", 7), ("2", 13), ("3", 19), ("4", 25), ("5", 31), ("6", 37), ("7", 43),
        ("8", 49), ("9", 55), ("0", 61), ("-", 67), ("=", 73), ("Backspace", 79),
        ("Insert", 85), ("Home", 91), ("PageUp", 97),
        ("Tab", 2), ("Q", 8), ("W", 14), ("E", 20), ("R", 26), ("T", 32), ("Y", 38), ("U", 44),
        ("I", 50), ("O", 56), ("P", 62), ("[", 68), ("]", 74), ("\\", 80),
        ("Delete", 86), ("End", 92), ("PageDown", 98),
        ("CapsLock", 3), ("A", 9), ("S", 15), ("D", 21), ("F", 27), ("G", 33), ("H", 39),
        ("J", 45), ("K", 51), ("L", 57), (";", 63), ("'", 69), ("Enter", 81),
        ("左Shift", 4), ("Z", 10), ("X", 16), ("C", 22), ("V", 28), ("B", 34), ("N", 40), ("M", 46),
        (",", 52), (".", 58), ("/", 64), ("右Shift", 82), ("↑", 94),
        ("左Ctrl", 5), ("Windows", 11), ("左Alt", 17), ("空格", 35), ("右Alt", 53), ("Fn", 59),
        ("菜单", 65), ("右Ctrl", 83), ("←", 89), ("↓", 95), ("→", 101)
    ];

    internal static byte[] CreateRequest()
    {
        var report = new byte[KeyboardIdentityReport.ReportLength];
        ReadHeader.CopyTo(report);
        return report;
    }

    internal static byte[]? ReadPayload(ReadOnlySpan<byte> report)
    {
        if (report.Length is not (KeyboardIdentityReport.ReportLength or ShortReportLength) ||
            report[0] != 0x06 || report[1] is not (0x86 or 0x06) || !report[2..8].SequenceEqual(ReadHeader[2..]))
            return null;

        // Legacy 010B transfers 384 bytes, with three 126-byte planes followed by six unassigned bytes.
        var payload = report.Slice(ReadHeader.Length, PayloadLength);
        if (!payload.ContainsAnyExcept((byte)0) || !payload.ContainsAnyExcept(byte.MaxValue))
            return null;
        return payload.ToArray();
    }

    internal static KeyboardPerKeyReadout? Decode(ReadOnlySpan<byte> report, ReadOnlySpan<byte> configuration) =>
        ReadPayload(report) is { } payload ? Describe(payload, configuration) : null;

    internal static KeyboardPerKeyReadout? Describe(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> configuration)
    {
        if (payload.Length != PayloadLength || configuration.Length != 128 ||
            configuration[56] != 0xFF || configuration[57] != 0xFF)
            return null;
        var colors = new Dictionary<string, uint>(LedIndices.Length, StringComparer.Ordinal);
        foreach (var (key, index) in LedIndices)
        {
            colors.Add(key, (uint)((payload[index] << 16) |
                (payload[PlaneLength + index] << 8) | payload[2 * PlaneLength + index]));
        }

        var active = IsCustomMode(configuration);
        string? revision = null;
        if (HasProfileSignature(configuration) && (active ||
            (configuration[9] == 0 && configuration[11] == CustomMode[2] &&
                KeyboardLightingReport.Describe(configuration).EditRevision is not null)))
        {
            Span<byte> state = stackalloc byte[PayloadLength + 128];
            payload.CopyTo(state);
            configuration.CopyTo(state[PayloadLength..]);
            revision = Convert.ToHexString(SHA256.HashData(state));
        }
        return new(new ReadOnlyDictionary<string, uint>(colors), active, revision);
    }

    internal static bool IsCustomMode(ReadOnlySpan<byte> configuration) =>
        configuration.Length == 128 && configuration.Slice(9, CustomMode.Length).SequenceEqual(CustomMode);

    internal static bool HasProfileSignature(ReadOnlySpan<byte> configuration) =>
        configuration.Length == 128 && configuration[56] == 0xFF && configuration[57] == 0xFF &&
        configuration[126] == 0x5A && configuration[127] == 0xA5;

    internal static byte[] EnableCustomMode(ReadOnlySpan<byte> configuration)
    {
        var desired = configuration.ToArray();
        CustomMode.CopyTo(desired.AsSpan(9));
        return desired;
    }

    internal static byte[]? ApplyColor(ReadOnlySpan<byte> original, string key, uint color)
    {
        if (original.Length != PayloadLength || color > 0xFFFFFF)
            return null;
        var keyIndex = Array.FindIndex(LedIndices, entry => entry.Key == key);
        if (keyIndex < 0)
            return null;
        var index = LedIndices[keyIndex].Index;
        var desired = original.ToArray();
        desired[index] = (byte)(color >> 16);
        desired[PlaneLength + index] = (byte)(color >> 8);
        desired[2 * PlaneLength + index] = (byte)color;
        // Uniform all-zero and all-FF banks are indistinguishable from unsupported device results.
        return desired.AsSpan().ContainsAnyExcept((byte)0) && desired.AsSpan().ContainsAnyExcept(byte.MaxValue)
            ? desired : null;
    }

    internal static byte[] CreateWriteRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
            throw new ArgumentException("Invalid keyboard color bank size.", nameof(payload));
        var report = CreateRequest();
        report[1] = 0x06;
        payload.CopyTo(report.AsSpan(ReadHeader.Length));
        return report;
    }
}
