// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class KeyboardHardwareIdentityFeaturesTests
{
    [Fact]
    public void ExactWiredShapeIsRequired()
    {
        Assert.True(KeyboardIdentityReport.Matches(0x258A, 0x010C, PeripheralConnection.Usb, 0xFF00, 1, 520,
            "BY Tech", "Gaming Keyboard"));
        Assert.False(KeyboardIdentityReport.Matches(0x258A, 0x010C, PeripheralConnection.Bluetooth, 0xFF00, 1, 520,
            "BY Tech", "Gaming Keyboard"));
        Assert.False(KeyboardIdentityReport.Matches(0x258A, 0x010C, PeripheralConnection.Usb, 0xFF00, 1, 6,
            "BY Tech", "Gaming Keyboard"));
        Assert.False(KeyboardIdentityReport.Matches(0x3554, 0xFA09, PeripheralConnection.Usb, 0xFF00, 1, 520,
            "BY Tech", "Gaming Keyboard"));
    }

    [Fact]
    public void RequestIsExactlyTheIdentityReadAndReplyRequiresTheMatchingHeader()
    {
        var reply = KeyboardIdentityReport.CreateRequest();
        Assert.Equal(520, reply.Length);
        Assert.Equal("0682010001000600", Convert.ToHexString(reply.AsSpan(0, 8)));
        Assert.False(reply.AsSpan(8).ContainsAnyExcept((byte)0));
        Assert.Null(KeyboardIdentityReport.Decode(reply));

        Convert.FromHexString("03000000010B").CopyTo(reply, 8);
        Assert.Equal("03000000010B", KeyboardIdentityReport.Decode(reply));
        Assert.Null(KeyboardIdentityReport.Decode(reply.AsSpan(0, 13)));
        reply[1] = 0x84;
        Assert.Null(KeyboardIdentityReport.Decode(reply));
    }

    [Fact]
    public void F87ProIdentityGivesTheKeyboardItsModel()
    {
        var device = Assert.Single(PeripheralIdentity.Identify([Keyboard("03000000010B")]));
        Assert.Equal("F87 Pro", device.Model);
        Assert.Equal("狼蛛", device.Manufacturer);
        Assert.Equal(PeripheralModelEvidence.HardwareReported, device.ModelEvidence);
        Assert.Equal("03000000010B", device.HardwareIdentityCode);
    }

    [Fact]
    public void UnknownHardwareIdentityDoesNotAssignAKnownModel()
    {
        var device = Assert.Single(PeripheralIdentity.Identify([Keyboard("03000000010C")]));
        Assert.Null(device.Model);
        Assert.Equal(PeripheralModelEvidence.Unknown, device.ModelEvidence);
        Assert.Equal("03000000010C", device.HardwareIdentityCode);
    }

    [Fact]
    public void ReceiverProductNameIsAvailableWithoutPromotingItToThePairedModel()
    {
        var receiver = new PeripheralInterface("receiver", "receiver", 0x5253, 0x1020, 1, 2,
            "RealTek", "MCHOSE K7 Ultra", PeripheralConnection.Usb);
        var device = Assert.Single(PeripheralIdentity.Identify([receiver]));
        Assert.Equal("MCHOSE K7 Ultra", device.ReceiverName);
        Assert.Null(device.Model);
        Assert.Equal(PeripheralPresence.ReceiverOnly, device.Presence);
    }

    private static PeripheralInterface Keyboard(string identity) =>
        new("keyboard", "keyboard", 0x258A, 0x010C, 0xFF00, 1, "BY Tech", "Gaming Keyboard",
            PeripheralConnection.Usb, identity);
}
