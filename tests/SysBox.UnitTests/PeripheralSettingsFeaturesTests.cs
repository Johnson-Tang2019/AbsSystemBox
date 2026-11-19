// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class PeripheralSettingsFeaturesTests
{
    [Fact]
    public void MouseReadRequiresTheSpecificReceiverCollection()
    {
        Assert.True(MchoseK7Reports.Matches(0x5253, 0x1020, PeripheralConnection.Usb, 0xFF01, 1, 65, "MCHOSE K7 Ultra"));
        Assert.False(MchoseK7Reports.Matches(0x5253, 0x1020, PeripheralConnection.Usb, 1, 2, 65, "MCHOSE K7 Ultra"));
        Assert.False(MchoseK7Reports.Matches(0x3837, 0x1012, PeripheralConnection.Usb, 0xFF01, 1, 65, "MCHOSE K7 Ultra"));
        Assert.False(MchoseK7Reports.Matches(0x5253, 0x1020, PeripheralConnection.Usb, 0xFF01, 1, 65, "MCHOSE Mouse"));
    }

    [Fact]
    public void MouseIdentityRequiresAConfirmedOnlineK7Ultra()
    {
        var reply = MchoseK7Reports.IdentityRequest();
        Assert.Equal(new byte[] { 0x11, 0xF9 }, reply[..2]);
        Assert.True(MchoseK7Reports.IsEmptyIdentity(reply));
        Assert.Null(MchoseK7Reports.DecodeIdentity(reply));

        PutMousePayload(reply, "373850411C050400095B00");
        var mouse = Assert.IsType<MousePeripheralReadout>(MchoseK7Reports.DecodeIdentity(reply));
        Assert.Equal(91, mouse.BatteryPercent);
        Assert.False(MchoseK7Reports.IsEmptyIdentity(reply));
        Assert.Null(MchoseK7Reports.DecodeIdentity(reply.AsSpan(0, 20)));

        reply[10] = 0xFE;
        Assert.Null(MchoseK7Reports.DecodeIdentity(reply));
        reply[10] = 0xF6;
        reply[4] = 0xFE;
        Assert.Null(MchoseK7Reports.DecodeIdentity(reply));
    }

    [Fact]
    public void ReceiverAndBodyKeepTheirOwnIdentities()
    {
        var receiver = new PeripheralInterface("receiver", "usb", 0x5253, 0x1020, 1, 2,
            "RealTek", "MCHOSE K7 Ultra", PeripheralConnection.Usb);
        var readout = new MousePeripheralReadout(91, "5.28.4.0");
        var vendorInterface = receiver with { Id = "feature", UsagePage = 0xFF01, Usage = 1, MouseReadout = readout };
        var device = Assert.Single(PeripheralIdentity.Identify([receiver, vendorInterface]));

        Assert.Equal("K7 Ultra", device.Model);
        Assert.Equal(PeripheralModelEvidence.HardwareReported, device.ModelEvidence);
        Assert.Equal("3837:4150", device.HardwareIdentityCode);
        Assert.Equal((ushort)0x5253, device.VendorId);
        Assert.Equal("MCHOSE K7 Ultra", device.ReceiverName);
        Assert.Equal(PeripheralPresence.Detected, device.Presence);
        Assert.Same(readout, device.MouseReadout);
        Assert.Equal(PeripheralPresence.ReceiverOnly, Assert.Single(PeripheralIdentity.Identify([receiver])).Presence);
    }

    [Fact]
    public void FirmwareUsesTheVersionStringWithMatchingReportAndLength()
    {
        var reply = MchoseK7Reports.FirmwareRequest();
        Assert.Null(MchoseK7Reports.DecodeFirmware(reply));
        PutMousePayload(reply, "08352E32382E342E30");
        Assert.Equal("5.28.4.0", MchoseK7Reports.DecodeFirmware(reply));
        reply[1] = 0xF9;
        Assert.Null(MchoseK7Reports.DecodeFirmware(reply));
    }

    [Fact]
    public void MouseSettingsDecodeCurrentRateAndBothDpiAxesWithBounds()
    {
        var reply = MchoseK7Reports.PerformanceRequest();
        Assert.True(MchoseK7Reports.IsEmptyPerformance(reply));
        Assert.Null(MchoseK7Reports.DecodePerformance(reply));
        PutMousePayload(reply, "004454042003E80340066009F00A800C06DE0803");
        PutMousePayload(reply, "2003E80340066009F00A800C", offset: 51);
        var current = Assert.IsType<MousePerformanceReadout>(MchoseK7Reports.DecodePerformance(reply));
        Assert.False(MchoseK7Reports.IsEmptyPerformance(reply));
        Assert.Equal([800, 1000, 1600, 2400, 2800, 3200], current.DpiStages);
        Assert.Equal(current.DpiStages, current.VerticalDpiStages);
        Assert.Equal(4, current.ActiveDpiStage);
        Assert.Equal(0, current.ActiveProfile);
        Assert.Equal(8000, current.PollingRateHz);
        Assert.Equal(2m, current.LiftOffDistanceMm);
        Assert.Equal(8, current.DebounceMilliseconds);
        Assert.Equal(3, current.SleepMinutes);
        Assert.Null(MchoseK7Reports.DecodePerformance(reply.AsSpan(0, 64)));
        reply[18] = 0xF8;
        Assert.Null(MchoseK7Reports.DecodePerformance(reply));
    }

    [Fact]
    public void KeyboardConfigurationRejectsEchoesAndStaysWithTheProIdentity()
    {
        var reply = KeyboardLightingReport.CreateRequest();
        Assert.Equal("0684000001008000", Convert.ToHexString(reply.AsSpan(0, 8)));
        Assert.Null(KeyboardLightingReport.Decode(reply));
        Convert.FromHexString("00030301000004040700012003").CopyTo(reply, 8);
        reply[64] = reply[65] = 0xFF;
        var lighting = Assert.IsType<KeyboardLightingReadout>(KeyboardLightingReport.Decode(reply));
        Assert.Equal(1, lighting.EffectId);
        Assert.Null(KeyboardLightingReport.Decode(reply.AsSpan(0, 136)));
        reply[4] = 2;
        Assert.Null(KeyboardLightingReport.Decode(reply));

        var keyboard = new PeripheralInterface("keyboard", "usb", 0x258A, 0x010C, 0xFF00, 1,
            "BY Tech", "Gaming Keyboard", PeripheralConnection.Usb, "03000000010B", KeyboardLighting: lighting);
        Assert.Same(lighting, Assert.Single(PeripheralIdentity.Identify([keyboard])).KeyboardLighting);
        Assert.Null(Assert.Single(PeripheralIdentity.Identify([keyboard with { HardwareIdentityCode = "03000000008F" }])).KeyboardLighting);
    }

    private static void PutMousePayload(byte[] report, string hex, int offset = 0)
    {
        var data = Convert.FromHexString(hex);
        for (var index = 0; index < data.Length; index++)
            report[2 + offset + index] = (byte)~data[index];
    }
}
