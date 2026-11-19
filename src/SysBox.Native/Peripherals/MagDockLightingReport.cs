// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using SysBox.Abstractions;

namespace SysBox.Native;

// Protocol evidence: OpenMouse Project mouse-protocol, bac031567b28f2a55c616fc525b03f2d5fa452cc,
// docs/mchose-protocol.md, "The MagDock — a second device, a second protocol".
internal static class MagDockLightingReport
{
    // The Windows MagDock descriptor reports 65 bytes for both directions, including report ID zero.
    internal const int ReportLength = 65;
    private const int HeaderLength = 7;
    private const int ReadPayloadLength = 30;
    private const int WritePayloadLength = 10;
    private const byte Magic = 0xaa;
    private const byte ReadCommand = 7;
    private const byte WriteCommand = 39;
    private const byte Response = 2;

    private const int Enabled = 0;
    private const int Mode = 1;
    private const int EffectCount = 2;
    private const int Speed = 3;
    private const int Brightness = 4;
    private const int MusicSync = 5;
    private const int Red = 6;
    private const int Green = 7;
    private const int Blue = 8;
    private const int WrittenDirection = 9;
    private const int ReadDirection = 29;
    private const int MusicMode = 5;
    private const int MaxLevel = 4;

    internal static bool Matches(ushort? vendor, ushort? product, ushort page, ushort usage,
        ushort inputLength, ushort outputLength) =>
        (vendor, product, page, usage, inputLength, outputLength) ==
            ((ushort)0x3837, (ushort)0x1012, (ushort)0xff00, (ushort)1, (ushort)ReportLength, (ushort)ReportLength);

    internal static byte[] CreateReadRequest() => BuildRequest(ReadCommand, 0);

    internal static Snapshot? Decode(ReadOnlySpan<byte> report)
    {
        // The Windows buffer begins with the implicit zero report ID.
        if (report.Length != ReportLength || report[0] != 0 || report[1] != Magic ||
            report[2] != ReadCommand || report[3] != Response || report[4] != 0 || report[5] != 0 ||
            report[6] != ReadPayloadLength)
            return null;

        var data = report.Slice(HeaderLength, ReadPayloadLength);
        if (data[Enabled] > 1 || data[MusicSync] > 1 || data[Mode] > MusicMode ||
            data[Mode] >= data[EffectCount] || data[Speed] > MaxLevel || data[Brightness] > MaxLevel)
            return null;
        return new Snapshot(data.ToArray());
    }

    private static byte[] BuildRequest(byte command, byte payloadLength)
    {
        var report = new byte[ReportLength];
        report[1] = Magic;
        report[2] = command;
        report[6] = payloadLength;
        return report;
    }

    internal sealed class Snapshot(byte[] data)
    {
        private bool IsEditable => data[Mode] < MusicMode && data[MusicSync] == 0;

        internal MagDockLightingReadout Describe()
        {
            var rgb = ((uint)data[Red] << 16) | ((uint)data[Green] << 8) | data[Blue];
            var revision = IsEditable ? Convert.ToHexString(SHA256.HashData(data)) : "";
            var modes = IsEditable
                ? Array.AsReadOnly(Enumerable.Range(0, Math.Min(MusicMode, (int)data[EffectCount])).ToArray())
                : (IReadOnlyList<int>)Array.Empty<int>();
            return new MagDockLightingReadout(data[Enabled] == 1, data[Mode], rgb,
                data[Brightness], data[Speed], revision, modes);
        }

        internal Snapshot? Apply(MagDockLightingChange change)
        {
            var mode = change.Mode ?? data[Mode];
            if (!IsEditable || mode < 0 || mode >= MusicMode || mode >= data[EffectCount] ||
                change.Brightness is < 0 or > MaxLevel || change.Speed is < 0 or > MaxLevel ||
                change.ColorRgb is > 0xffffff || (change.ColorRgb is not null && mode > 1))
                return null;

            var next = data.ToArray();
            if (change.IsEnabled is { } enabled)
                next[Enabled] = enabled ? (byte)1 : (byte)0;
            next[Mode] = (byte)mode;
            if (change.Brightness is { } brightness)
                next[Brightness] = (byte)brightness;
            if (change.Speed is { } speed)
                next[Speed] = (byte)speed;
            if (change.ColorRgb is { } rgb)
            {
                next[Red] = (byte)(rgb >> 16);
                next[Green] = (byte)(rgb >> 8);
                next[Blue] = (byte)rgb;
            }
            return new Snapshot(next);
        }

        internal byte[]? CreateWriteRequest()
        {
            if (!IsEditable)
                return null;
            var report = BuildRequest(WriteCommand, WritePayloadLength);
            data.AsSpan(0, WrittenDirection).CopyTo(report.AsSpan(HeaderLength));
            report[HeaderLength + WrittenDirection] = data[ReadDirection];
            return report;
        }

        internal bool SameSettings(Snapshot other) =>
            data.AsSpan(0, WrittenDirection).SequenceEqual(other.Data.AsSpan(0, WrittenDirection)) &&
            data[ReadDirection] == other.Data[ReadDirection];

        internal bool CanRestoreAfter(Snapshot desired, Snapshot observed)
        {
            if (!observed.IsEditable)
                return false;
            for (var index = 0; index < data.Length; index++)
            {
                // A partial update can contain old and requested bytes, never a third value.
                if (observed.Data[index] != data[index] && observed.Data[index] != desired.Data[index])
                    return false;
            }
            return true;
        }

        private byte[] Data => data;
    }
}
