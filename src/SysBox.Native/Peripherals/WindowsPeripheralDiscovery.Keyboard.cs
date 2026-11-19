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
        if (!IsKeyboardDescriptor(device))
            return default;
        var identity = ReadKeyboardIdentity(device, token);
        var lighting = KeyboardIdentityReport.Model(identity) is not null && ReadKeyboardConfiguration(device, token) is { } payload
            ? KeyboardLightingReport.Describe(payload)
            : null;
        return (identity, lighting);
    }

    private static Result<KeyboardLightingReadout> WriteKeyboardLighting(string path, string expectedRevision,
        KeyboardLightingChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!IsKeyboardDescriptor(device))
            return OperationFailure<KeyboardLightingReadout>("无法打开键盘配置接口。请确认有线连接，关闭其他配置程序后刷新。");
        var identity = ReadKeyboardIdentity(device, token);
        if (KeyboardIdentityReport.Model(identity) is null)
            return OperationFailure<KeyboardLightingReadout>("无法确认是支持的狼蛛 F87 Pro，请刷新设备。");
        var original = ReadKeyboardConfiguration(device, token);
        if (original is null)
            return OperationFailure<KeyboardLightingReadout>("无法读取键盘的原始灯光配置。");
        var current = KeyboardLightingReport.Describe(original);
        if (current.EditRevision is null || !string.Equals(expectedRevision, current.EditRevision, StringComparison.Ordinal))
            return OperationFailure<KeyboardLightingReadout>("灯光配置已改变或不可编辑，请刷新后重试。");
        var desired = KeyboardLightingReport.ApplyChange(original, change);
        if (desired is null)
            return Result<KeyboardLightingReadout>.Failure(new SysBoxError(ErrorCodes.InvalidArgument,
                "当前灯效不支持所选灯光参数。"));
        if (desired.AsSpan().SequenceEqual(original))
            return Result<KeyboardLightingReadout>.Success(current);

        token.ThrowIfCancellationRequested();
        SendKeyboardConfiguration(device, desired);

        // Once the report has been sent, finish bounded verification under the same exclusive handle.
        var observed = ReadKeyboardConfiguration(device, CancellationToken.None);
        if (observed is not null && observed.AsSpan().SequenceEqual(desired))
            return Result<KeyboardLightingReadout>.Success(KeyboardLightingReport.Describe(observed));
        if (observed is not null && observed.AsSpan().SequenceEqual(original))
            return OperationFailure<KeyboardLightingReadout>("灯光设置未应用，原设置已核对。");

        if (observed is not null && KeyboardLightingReport.CanRestore(original, desired, observed) &&
            string.Equals(ReadKeyboardIdentity(device, CancellationToken.None), identity, StringComparison.Ordinal) &&
            ReadKeyboardConfiguration(device, CancellationToken.None) is { } latest &&
            KeyboardLightingReport.CanRestore(original, desired, latest))
        {
            if (latest.AsSpan().SequenceEqual(desired))
                return Result<KeyboardLightingReadout>.Success(KeyboardLightingReport.Describe(latest));
            if (latest.AsSpan().SequenceEqual(original))
                return OperationFailure<KeyboardLightingReadout>("灯光设置未应用，原设置已核对。");

            SendKeyboardConfiguration(device, original);
            var restored = ReadKeyboardConfiguration(device, CancellationToken.None);
            if (restored is not null && restored.AsSpan().SequenceEqual(original))
                return OperationFailure<KeyboardLightingReadout>("灯光设置未通过核验，已恢复并核对写入前的设置。");
        }

        return OperationFailure<KeyboardLightingReadout>("无法确认灯光设置结果。请先刷新核对键盘当前配置。");
    }

    private static bool IsKeyboardDescriptor(SafeFileHandle device)
    {
        var ids = new HidAttributes { Size = 12 };
        return !device.IsInvalid && HidD_GetAttributes(device, ref ids) && ReadCapabilities(device) is { } caps &&
            KeyboardIdentityReport.MatchesDescriptor(ids.VendorId, ids.ProductId,
                caps.UsagePage, caps.Usage, caps.FeatureReportLength);
    }

    private static string? ReadKeyboardIdentity(SafeFileHandle device, CancellationToken token) =>
        KeyboardIdentityReport.Decode(ReadFeatureReply(device, KeyboardIdentityReport.CreateRequest(), token));

    private static byte[]? ReadKeyboardConfiguration(SafeFileHandle device, CancellationToken token) =>
        KeyboardLightingReport.ReadPayload(ReadFeatureReply(device, KeyboardLightingReport.CreateRequest(), token));

    private static void SendKeyboardConfiguration(SafeFileHandle device, byte[] payload)
    {
        var report = KeyboardLightingReport.CreateWriteRequest(payload);
        // Only readback can establish whether the device accepted a report.
        HidD_SetFeature(device, report, (uint)report.Length);
        Thread.Sleep(40);
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
