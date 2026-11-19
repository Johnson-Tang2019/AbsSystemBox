// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class PeripheralConfigurationTests
{
    [Fact]
    public void MouseDpiWritePreservesIndexesReservedByteAndOtherStages()
    {
        var reply = MouseReport();
        var edit = Assert.IsType<MchoseK7Reports.DpiEdit>(MchoseK7Reports.PrepareDpiEdit(reply, "5.28.4.0", 4, 3000));
        var command = edit.ApplyRequest;
        Assert.Equal(new byte[] { 0x11, 0xBF, 0xFE, 0xFB, 0xFB }, command[..5]);
        Assert.Equal(0xF9, command[17]);
        Assert.True(edit.IsOriginal(reply));
        Assert.False(edit.IsApplied(reply));
        PutDpi(reply, 4, 3000);
        Assert.True(edit.IsApplied(reply));
        Assert.NotNull(edit.CreateRestoreRequest(reply));
        reply[21] ^= 1;
        Assert.False(edit.IsApplied(reply));
        Assert.Null(edit.CreateRestoreRequest(reply));
    }

    [Fact]
    public void MouseEditRequiresTheVerifiedFirmwareAndMatchingAxes()
    {
        var reply = MouseReport();
        Assert.NotNull(MchoseK7Reports.DecodePerformance(reply, "5.28.4.0")?.EditRevision);
        Assert.Null(MchoseK7Reports.PrepareDpiEdit(reply, "5.28.3.0", 4, 3000));
        Assert.Null(MchoseK7Reports.PrepareDpiEdit(reply, "5.28.4.0", 4, 3001));
        reply[53] ^= 1;
        Assert.Null(MchoseK7Reports.DecodePerformance(reply, "5.28.4.0")?.EditRevision);
        Assert.Null(MchoseK7Reports.PrepareDpiEdit(reply, "5.28.4.0", 4, 3000));
    }

    [Fact]
    public void KeyboardPaletteAndBrightnessModifyOnlyTheActiveEffectRecord()
    {
        var payload = KeyboardPayload();
        var before = Assert.IsType<KeyboardLightingReadout>(KeyboardLightingReport.Describe(payload));
        Assert.Equal(4, before.BrightnessLevel);
        Assert.Equal(0, before.PaletteIndex);
        Assert.Null(before.SpeedLevel);
        var changed = Assert.IsType<byte[]>(KeyboardLightingReport.ApplyChange(payload,
            new KeyboardLightingChange(1, BrightnessLevel: 3, PaletteIndex: 2)));
        var expected = payload.ToArray();
        expected[58] = 3;
        expected[59] = 0x42;
        Assert.Equal(expected, changed);
        var write = KeyboardLightingReport.CreateWriteRequest(changed);
        Assert.Equal(new byte[] { 0x06, 0x04, 0, 0, 1, 0, 0x80, 0 }, write[..8]);
        Assert.Equal(changed, write[8..136]);
        Assert.All(write[136..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void KeyboardRejectsHiddenEffectsAndUnsupportedSettingChanges()
    {
        var payload = KeyboardPayload();
        Assert.Null(KeyboardLightingReport.ApplyChange(payload, new KeyboardLightingChange(6)));
        Assert.Null(KeyboardLightingReport.ApplyChange(payload, new KeyboardLightingChange(1, SpeedLevel: 2)));
        Assert.Null(KeyboardLightingReport.ApplyChange(payload, new KeyboardLightingChange(3, PaletteIndex: 1)));
        Assert.Null(KeyboardLightingReport.ApplyChange(payload, new KeyboardLightingChange(1, PaletteIndex: 8)));
    }

    [Fact]
    public void KeyboardCannotRestoreOverAnUnrelatedChangeInTheSameByte()
    {
        var payload = KeyboardPayload();
        var desired = KeyboardLightingReport.ApplyChange(payload, new KeyboardLightingChange(1, PaletteIndex: 3))!;
        var observed = payload.ToArray();
        observed[59] = 0x41;
        Assert.True(KeyboardLightingReport.CanRestore(payload, desired, observed));
        observed[59] = 0x31;
        Assert.False(KeyboardLightingReport.CanRestore(payload, desired, observed));
    }

    [Fact]
    public void ConfigurationPathRequiresOneDiscoveredWritableEndpoint()
    {
        var data = MchoseK7Reports.DecodePerformance(MouseReport(), "5.28.4.0")!;
        var group = "container:test";
        var endpoint = new PeripheralInterface("first", group, 0x5253, 0x1020, 0xFF01, 1,
            "MCHOSE", MchoseK7Reports.ReceiverName, PeripheralConnection.Usb, MouseReadout: new MousePeripheralReadout(80, "5.28.4.0", data));
        var discovered = Assert.Single(PeripheralIdentity.Identify([endpoint]));
        Assert.Equal("first", WindowsPeripheralDiscovery.SelectWritableInterfaces([endpoint], [discovered])[discovered.Id]);
        Assert.Empty(WindowsPeripheralDiscovery.SelectWritableInterfaces([endpoint, endpoint with { Id = "second" }], [discovered]));
        Assert.Empty(WindowsPeripheralDiscovery.SelectWritableInterfaces([endpoint], []));
    }

    private static byte[] MouseReport()
    {
        var reply = MchoseK7Reports.PerformanceRequest();
        reply[3] = 0xDB;
        reply[4] = 0xAE;
        reply[5] = 0xFB;
        reply[18] = 0xF9;
        int[] stages = [800, 1000, 1600, 2400, 2800, 3200];
        for (var index = 0; index < stages.Length; index++)
            PutDpi(reply, index, stages[index]);
        return reply;
    }

    private static void PutDpi(byte[] reply, int stage, int dpi)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6 + stage * 2), (ushort)~dpi);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(53 + stage * 2), (ushort)~dpi);
    }

    private static byte[] KeyboardPayload()
    {
        var payload = new byte[128];
        payload[10] = 1;
        payload[56] = payload[57] = 0xFF;
        payload[58] = payload[60] = 4;
        payload[59] = 0x40;
        payload[61] = 0x43;
        payload[126] = 0x5A;
        payload[127] = 0xA5;
        return payload;
    }
}
