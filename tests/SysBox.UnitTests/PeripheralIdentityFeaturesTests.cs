// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class PeripheralIdentityFeaturesTests
{
    [Fact]
    public void CompositeCollectionsMergeWithoutCombiningTwoPhysicalDevices()
    {
        var input = Collection("keyboard-1", "container-a", 0x258a, 0x010c, 6,
            productName: "HID Keyboard Device");
        var settings = Collection("settings-1", "CONTAINER-A", 0x258a, 0x010c, 1,
            usagePage: 0xff00, productName: "AULA F75");
        var secondKeyboard = input with { Id = "keyboard-2", GroupId = "container-b", ProductName = "AULA F75" };

        var devices = PeripheralIdentity.Identify([input, settings, secondKeyboard]);
        var reversed = PeripheralIdentity.Identify([secondKeyboard, settings, input]);

        Assert.Equal(2, devices.Count);
        Assert.NotEqual(devices[0].Id, devices[1].Id);
        Assert.Equal(devices.Select(device => device.Id), reversed.Select(device => device.Id));
        Assert.All(devices, device =>
        {
            Assert.Equal(PeripheralKind.Keyboard, device.Kind);
            Assert.Equal("AULA F75", device.Model);
            Assert.Equal("狼蛛", device.Manufacturer);
            Assert.Equal(PeripheralModelEvidence.SystemReported, device.ModelEvidence);
            Assert.DoesNotContain("container-", device.Id, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void StandardInputUsagesClassifyWithoutInventingBrandOrModel()
    {
        var mouse = Collection("mouse", "container-a", 0x1234, 0x0101, 2,
            productName: "HID-compliant mouse");
        var keyboard = Collection("keyboard", "container-a", 0x1234, 0x0101, 7,
            productName: "USB Composite Device") with { Manufacturer = "(标准键盘)" };
        var gamepad = Collection("gamepad", "container-b", 0x045e, 0x028e, 4,
            productName: "Controller (XBOX 360 For Windows)");

        var devices = PeripheralIdentity.Identify([mouse, keyboard, gamepad]);

        Assert.Equal(new[] { PeripheralKind.Mouse, PeripheralKind.Keyboard, PeripheralKind.Gamepad },
            devices.Select(device => device.Kind));
        Assert.All(devices, device =>
        {
            Assert.Null(device.Manufacturer);
            Assert.Null(device.Model);
            Assert.Equal(PeripheralModelEvidence.Unknown, device.ModelEvidence);
        });
    }

    [Fact]
    public void SharedUsbIdsDoNotNameAnUnconfirmedKeyboard()
    {
        var wired = Collection("keyboard", "wired", 0x258a, 0x010c, 0x06, productName: "Gaming Keyboard");
        var wireless = Collection("receiver", "wireless", 0x3554, 0xfa09, 0x02,
            productName: "USB Receiver");

        var devices = PeripheralIdentity.Identify([wired, wireless]);

        Assert.Equal(2, devices.Count);
        Assert.All(devices, device =>
        {
            Assert.Equal(PeripheralKind.Keyboard, device.Kind);
            Assert.Null(device.Manufacturer);
            Assert.Null(device.Model);
            Assert.Equal(PeripheralModelEvidence.Unknown, device.ModelEvidence);
        });
        var receiver = Assert.Single(devices, device => device.Presence == PeripheralPresence.ReceiverOnly);
        Assert.Equal(PeripheralConnection.WirelessReceiver, receiver.Connection);
    }

    [Fact]
    public void KnownReceiverDoesNotNameThePairedPeripheralEvenWithASystemLabel()
    {
        var input = Collection("mouse", "container", 0x5253, 0x1020, 2, productName: "MCHOSE L7 Pro");
        var settings = Collection("vendor", "container", 0x5253, 0x1020, 1, usagePage: 0xff00);

        var device = Assert.Single(PeripheralIdentity.Identify([input, settings]));

        Assert.Equal(PeripheralKind.Mouse, device.Kind);
        Assert.Equal("迈从", device.Manufacturer);
        Assert.Equal(PeripheralConnection.WirelessReceiver, device.Connection);
        Assert.Equal(PeripheralPresence.ReceiverOnly, device.Presence);
        Assert.Null(device.Model);
        Assert.Equal(PeripheralModelEvidence.Unknown, device.ModelEvidence);
    }

    [Fact]
    public void BluetoothTopologyKeepsItsConnectionWhenUsbCatalogIdsAreReused()
    {
        var bluetooth = Collection("keyboard", "container", 0x3554, 0xfa09, 6,
            productName: "AULA F75") with { Connection = PeripheralConnection.Bluetooth };

        var device = Assert.Single(PeripheralIdentity.Identify([bluetooth]));

        Assert.Equal(PeripheralConnection.Bluetooth, device.Connection);
        Assert.Equal(PeripheralPresence.Detected, device.Presence);
        Assert.Equal(PeripheralModelEvidence.SystemReported, device.ModelEvidence);
    }

    [Fact]
    public void CatalogEvidenceIsSeparateFromASpecificSystemLabel()
    {
        var mouse = Collection("mouse", "container", 0x3837, 0x418c, 2, productName: "Gaming Mouse");

        var catalogMatch = Assert.Single(PeripheralIdentity.Identify([mouse]));
        var reported = Assert.Single(PeripheralIdentity.Identify([mouse with { ProductName = "MCHOSE G3 V2" }]));

        Assert.Equal("G3 V2", catalogMatch.Model);
        Assert.Equal(PeripheralModelEvidence.CatalogMatched, catalogMatch.ModelEvidence);
        Assert.Equal("MCHOSE G3 V2", reported.Model);
        Assert.Equal(PeripheralModelEvidence.SystemReported, reported.ModelEvidence);
        Assert.Equal(catalogMatch.Id, reported.Id);
    }

    [Fact]
    public void VendorInterfaceUsesAnInputGroupOnlyWhenItsCategoryIsUnambiguous()
    {
        var vendor = Collection("vendor", "container", 0x1234, 0x0100, 1, usagePage: 0xff00,
            productName: "AULA F75");
        var keyboard = Collection("keyboard", "container", 0x1234, 0x0100, 6);
        var mouse = Collection("mouse", "container", 0x1234, 0x0100, 2);

        Assert.Empty(PeripheralIdentity.Identify([vendor]));
        Assert.Equal("AULA F75", Assert.Single(PeripheralIdentity.Identify([keyboard, vendor])).Model);
        Assert.All(PeripheralIdentity.Identify([keyboard, mouse, vendor]), device => Assert.Null(device.Model));
    }

    [Fact]
    public void VendorAndConsumerCollectionsOutsideTheCatalogAreExcluded()
    {
        var flydigiDock = Collection("dock", "container-a", 0x37d7, 0x6001, 1,
            usagePage: 0xffa0, productName: "Flydigi CD2");
        var consumer = Collection("consumer", "container-b", 0x3554, 0xfa09, 1, usagePage: 0x0c);

        Assert.Empty(PeripheralIdentity.Identify([flydigiDock, consumer]));
    }

    [Fact]
    public void FlydigiUsbVendorEndpointLeavesTransportUnknown()
    {
        var vendor = Collection("vendor", "container", 0x37d7, 0x2401, 1,
            usagePage: 0xffa0, productName: "Flydigi Vader 5 Pro");
        var emulatedKeyboard = Collection("keyboard", "container", 0x37d7, 0x2401, 6);

        var device = Assert.Single(PeripheralIdentity.Identify([vendor, emulatedKeyboard]));

        Assert.Equal(PeripheralKind.Gamepad, device.Kind);
        Assert.Equal("飞智", device.Manufacturer);
        Assert.Equal(PeripheralConnection.Unknown, device.Connection);
        Assert.Equal("Flydigi Vader 5 Pro", device.Model);
        Assert.Equal(PeripheralModelEvidence.SystemReported, device.ModelEvidence);
    }

    private static PeripheralInterface Collection(
        string id, string group, ushort vendor, ushort product, ushort usage,
        ushort usagePage = 0x01, string? productName = null) =>
        new(id, group, vendor, product, usagePage, usage, null, productName, PeripheralConnection.Usb);
}
