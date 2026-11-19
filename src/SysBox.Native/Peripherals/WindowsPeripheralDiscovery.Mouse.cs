// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static MousePeripheralReadout? ReadMouse(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid)
            return null;
        var ids = new HidAttributes { Size = 12 };
        if (!HidD_GetAttributes(device, ref ids) || ReadCapabilities(device) is not { } caps ||
            !MchoseK7Reports.MatchesDescriptor(ids.VendorId, ids.ProductId,
                caps.UsagePage, caps.Usage, caps.FeatureReportLength))
            return null;
        var product = new byte[512];
        if (!HidD_GetProductString(device, product, (uint)product.Length) ||
            !string.Equals(ReadText(product), MchoseK7Reports.ReceiverName, StringComparison.OrdinalIgnoreCase))
            return null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = ReadFeatureReply(device, MchoseK7Reports.IdentityRequest(), token);
            if (MchoseK7Reports.DecodeIdentity(response) is { } mouse)
                return ReadMouseSettings(device, mouse, token);
            if (!MchoseK7Reports.IsEmptyIdentity(response) || attempt == 3)
                break;
            Thread.Sleep(120);
        }
        return null;
    }

    private static MousePeripheralReadout ReadMouseSettings(SafeFileHandle device,
        MousePeripheralReadout mouse, CancellationToken token)
    {
        // The K7 needs additional time between feature-report exchanges.
        Thread.Sleep(30);
        var firmware = MchoseK7Reports.DecodeFirmware(ReadFeatureReply(device, MchoseK7Reports.FirmwareRequest(), token));
        Thread.Sleep(30);
        var request = MchoseK7Reports.PerformanceRequest();
        var response = ReadFeatureReply(device, request, token);
        if (MchoseK7Reports.IsEmptyPerformance(response))
        {
            Thread.Sleep(120);
            response = ReadFeatureReply(device, request, token);
        }
        return mouse with { FirmwareVersion = firmware, Performance = MchoseK7Reports.DecodePerformance(response) };
    }
}
