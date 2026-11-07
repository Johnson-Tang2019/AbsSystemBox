// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

/// <summary>Independent implementation of the observed five-byte display frame.</summary>
public static class GoldenFiledDisplayProtocol
{
    public static byte[] Encode(int temperature, int fanRpm)
    {
        if (temperature is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(temperature));
        if (fanRpm is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(fanRpm));
        // Header is literal. Only the three payload bytes are XOR encoded.
        return [0x74, 0x00, (byte)(temperature ^ 2), (byte)((fanRpm >> 8) ^ 2), (byte)((fanRpm & 255) ^ 2)];
    }
}
