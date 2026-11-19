// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static (string? Code, KeyboardLightingReadout? Lighting) ReadKeyboard(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid)
            return default;
        var ids = new HidAttributes { Size = 12 };
        if (!HidD_GetAttributes(device, ref ids) || ReadCapabilities(device) is not { } caps ||
            !KeyboardIdentityReport.MatchesDescriptor(ids.VendorId, ids.ProductId,
                caps.UsagePage, caps.Usage, caps.FeatureReportLength))
            return default;

        var identity = KeyboardIdentityReport.Decode(ReadFeatureReply(device, KeyboardIdentityReport.CreateRequest(), token));
        var lighting = KeyboardIdentityReport.Model(identity) is null ? null :
            KeyboardLightingReport.Decode(ReadFeatureReply(device, KeyboardLightingReport.CreateRequest(), token));
        return (identity, lighting);
    }

    // The fixed SET_FEATURE selectors request reports for GET_FEATURE. They do not change settings.
    private static byte[]? ReadFeatureReply(SafeFileHandle device, byte[] request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HidD_SetFeature(device, request, (uint)request.Length))
            return null;
        Thread.Sleep(40);
        token.ThrowIfCancellationRequested();

        var response = new byte[request.Length];
        response[0] = request[0];
        return HidD_GetFeature(device, response, (uint)response.Length)
            ? response : null;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, [In] byte[] buffer, uint length);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, [In, Out] byte[] buffer, uint length);
}
