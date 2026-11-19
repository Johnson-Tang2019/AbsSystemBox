// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class KeyboardPerKeyRgbTests
{
    [Theory]
    [InlineData(520, 0x86)]
    [InlineData(392, 0x06)]
    public void ReadsThe87ResourceKeysFromDistinct126BytePlanes(int reportLength, int replyCommand)
    {
        var report = CreateReply();
        report[1] = (byte)replyCommand;
        var result = Assert.IsType<KeyboardPerKeyReadout>(
            KeyboardPerKeyRgbReport.Decode(report.AsSpan(0, reportLength), Configuration(1, 21, 32)));

        Assert.Equal(87, result.Colors.Count);
        Assert.Equal(87, result.Colors.Values.Distinct().Count());
        Assert.Equal(ColorAt(0), result.Colors["Esc"]);
        Assert.Equal(ColorAt(14), result.Colors["W"]);
        Assert.Equal(ColorAt(81), result.Colors["Enter"]);
        Assert.Equal(ColorAt(91), result.Colors["Home"]);
        Assert.Equal(ColorAt(53), result.Colors["右Alt"]);
        Assert.Equal(ColorAt(59), result.Colors["Fn"]);
        Assert.Equal(ColorAt(83), result.Colors["右Ctrl"]);
        Assert.Equal(ColorAt(101), result.Colors["→"]);
        Assert.True(result.IsActive);
        Assert.NotNull(result.EditRevision);
    }

    [Fact]
    public void UnassignedTailAndTransportPaddingNeverBecomeColors()
    {
        var report = CreateReply();
        var configuration = Configuration(0, 1, 32);
        var initial = Assert.IsType<KeyboardPerKeyReadout>(KeyboardPerKeyRgbReport.Decode(report, configuration));
        Convert.FromHexString("DEADBEEF1234").CopyTo(report, 386);
        report.AsSpan(392).Fill(0xFC);
        var read = Assert.IsType<KeyboardPerKeyReadout>(KeyboardPerKeyRgbReport.Decode(report, configuration));

        Assert.Equal(initial.Colors.OrderBy(color => color.Key), read.Colors.OrderBy(color => color.Key));
        Assert.False(read.IsActive);
    }

    [Fact]
    public void RejectsTruncatedForeignLengthAndUninitializedReplies()
    {
        var configuration = Configuration(0, 1, 32);
        var report = CreateReply();
        Assert.Equal(new byte[] { 6, 0x86, 0, 0, 1, 0, 0x80, 1 }, report[..8]);
        Assert.Null(KeyboardPerKeyRgbReport.Decode(report.AsSpan(0, 391), configuration));

        report[6] = 0x7A;
        Assert.Null(KeyboardPerKeyRgbReport.Decode(report, configuration));
        report[6] = 0x80;
        report[2] = 1;
        Assert.Null(KeyboardPerKeyRgbReport.Decode(report, configuration));

        report = KeyboardPerKeyRgbReport.CreateRequest();
        Assert.Null(KeyboardPerKeyRgbReport.Decode(report, configuration));
        report.AsSpan(8, 384).Fill(byte.MaxValue);
        Assert.Null(KeyboardPerKeyRgbReport.Decode(report, configuration));

        configuration[57] = 0;
        Assert.Null(KeyboardPerKeyRgbReport.Decode(CreateReply(), configuration));
    }

    [Theory]
    [InlineData(0, 21, 32)]
    [InlineData(1, 1, 32)]
    [InlineData(1, 21, 33)]
    public void OnlyCustomBankZeroIsReportedActive(int flag, int effect, int bank)
    {
        var result = Assert.IsType<KeyboardPerKeyReadout>(
            KeyboardPerKeyRgbReport.Decode(CreateReply(), Configuration(flag, effect, bank)));
        Assert.False(result.IsActive);
    }

    [Fact]
    public void OnlyExactWiredIdentityCarriesWritableReadout()
    {
        var buttons = Assert.IsType<KeyboardPerKeyReadout>(
            KeyboardPerKeyRgbReport.Decode(CreateReply(), Configuration(0, 1, 32)));
        var endpoint = new PeripheralInterface("keyboard", "container", 0x258A, 0x010C, 0xFF00, 1,
            "BY Tech", "Gaming Keyboard", PeripheralConnection.Usb, "03000000010B", KeyboardPerKey: buttons);
        var devices = PeripheralIdentity.Identify([endpoint]);
        var valid = Assert.Single(devices);

        Assert.Equal(PeripheralModelEvidence.HardwareReported, valid.ModelEvidence);
        Assert.Same(buttons, valid.KeyboardPerKey);
        Assert.Equal(endpoint.Id, WindowsPeripheralDiscovery.SelectWritableInterfaces([endpoint], devices)[valid.Id]);

        foreach (var other in new[]
        {
            endpoint with { HardwareIdentityCode = null },
            endpoint with { HardwareIdentityCode = "03000000008F" },
            endpoint with { HardwareIdentityCode = "0300000000A9" },
            endpoint with { Connection = PeripheralConnection.WirelessReceiver }
        })
        {
            var otherDevices = PeripheralIdentity.Identify([other]);
            Assert.Null(Assert.Single(otherDevices).KeyboardPerKey);
            Assert.Empty(WindowsPeripheralDiscovery.SelectWritableInterfaces([other], otherDevices));
        }
    }

    [Fact]
    public void EncodesOneMappedKeyAndPreservesTheFullColorBank()
    {
        var original = KeyboardPerKeyRgbReport.ReadPayload(CreateReply())!;
        Convert.FromHexString("DEADBEEF1234").CopyTo(original, 378);
        var change = KeyboardPerKeyRgbReport.ApplyColor(original, "Enter", 0x4DB8C3)!;
        var modifiedOffsets = new[] { 81, 207, 333 };
        Assert.Equal(modifiedOffsets, Enumerable.Range(0, original.Length).Where(index => original[index] != change[index]));
        Assert.Equal(new byte[] { 0x4D, 0xB8, 0xC3 }, modifiedOffsets.Select(index => change[index]));
        var packet = KeyboardPerKeyRgbReport.CreateWriteRequest(change);
        Assert.Equal(new byte[] { 6, 6, 0, 0, 1, 0, 0x80, 1 }, packet[..8]);
        Assert.Equal(change, packet[8..392]);
        Assert.All(packet[392..], value => Assert.Equal(0, value));
        Assert.Null(KeyboardPerKeyRgbReport.ApplyColor(original, "Unknown", 1));
        Assert.Null(KeyboardPerKeyRgbReport.ApplyColor(original, "Enter", 0x1000000));
    }

    [Fact]
    public void CanEnterAndLeaveCustomLightingWithRevisionsBoundToBothReports()
    {
        var payload = KeyboardPerKeyRgbReport.ReadPayload(CreateReply())!;
        var preset = Configuration(0, 1, 32);
        var original = KeyboardPerKeyRgbReport.Describe(payload, preset)!;
        var custom = KeyboardPerKeyRgbReport.EnableCustomMode(preset);
        Assert.Equal(new byte[] { 1, 21, 32 }, custom[9..12]);
        Assert.Equal(preset[..9], custom[..9]);
        Assert.Equal(preset[12..], custom[12..]);
        var active = KeyboardPerKeyRgbReport.Describe(payload, custom)!;
        Assert.True(active.IsActive);
        Assert.NotEqual(original.EditRevision, active.EditRevision);
        Assert.NotNull(KeyboardLightingReport.Describe(custom).EditRevision);
        var edited = KeyboardPerKeyRgbReport.ApplyColor(payload, "Esc", 0x14AB27)!;
        Assert.NotEqual(active.EditRevision, KeyboardPerKeyRgbReport.Describe(edited, custom)!.EditRevision);
        Assert.Equal(preset, KeyboardLightingReport.ApplyChange(custom, new(1)));
        Assert.Null(KeyboardPerKeyRgbReport.Describe(payload, Configuration(1, 21, 33))!.EditRevision);

        custom[126] = 0;
        Assert.True(KeyboardPerKeyRgbReport.Describe(payload, custom)!.IsActive);
        Assert.Null(KeyboardPerKeyRgbReport.Describe(payload, custom)!.EditRevision);
        Assert.Null(KeyboardLightingReport.ApplyChange(custom, new(1)));
    }

    private static byte[] Configuration(int flag, int effect, int bank)
    {
        var configuration = new byte[128];
        configuration[9] = (byte)flag;
        configuration[10] = (byte)effect;
        configuration[11] = (byte)bank;
        configuration[56] = configuration[57] = 0xFF;
        configuration[126] = 0x5A;
        configuration[127] = 0xA5;
        return configuration;
    }

    private static byte[] CreateReply()
    {
        var report = KeyboardPerKeyRgbReport.CreateRequest();
        for (var led = 0; led < 126; led++)
        {
            var color = ColorAt(led);
            report[8 + led] = (byte)(color >> 16);
            report[134 + led] = (byte)(color >> 8);
            report[260 + led] = (byte)color;
        }

        return report;
    }

    private static uint ColorAt(int led) => (uint)((led << 16) | ((254 - led) << 8) | (led ^ 0xA5));
}
