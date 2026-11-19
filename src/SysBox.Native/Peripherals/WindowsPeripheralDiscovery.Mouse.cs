// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static MousePeripheralReadout? ReadMouse(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = OpenMouseReceiver(path);
        if (!IsMouseReceiver(device) || ReadMouseIdentity(device, token) is not { } mouse)
            return null;

        var firmware = ReadMouseFirmware(device, token);
        return mouse with
        {
            FirmwareVersion = firmware,
            Performance = MchoseK7Reports.DecodePerformance(ReadMouseConfiguration(device, token), firmware)
        };
    }

    private static Result<MousePerformanceReadout> WriteMouseDpi(string path, string expectedRevision,
        int stage, int dpi, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = OpenMouseReceiver(path);
        if (!IsMouseReceiver(device) || ReadMouseIdentity(device, token) is null)
            return DpiWriteFailure(ErrorCodes.Unavailable, "暂未确认已连接的迈从 K7 Ultra，请唤醒设备后刷新。");

        var firmware = ReadMouseFirmware(device, token);
        var snapshot = ReadMouseConfiguration(device, token);
        var original = MchoseK7Reports.DecodePerformance(snapshot, firmware);
        if (original?.EditRevision is not { } revision || !string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return DpiWriteFailure(ErrorCodes.Unavailable, "鼠标配置已变化或暂不可编辑，请刷新后重试。");

        var edit = MchoseK7Reports.PrepareDpiEdit(snapshot, firmware, stage, dpi);
        if (edit is null)
            return DpiWriteFailure(ErrorCodes.InvalidArgument, "请选择已启用的 DPI 档位，并以 50 为步长输入 50 到 42000 的数值。");
        if (edit.IsApplied(snapshot))
            return Result<MousePerformanceReadout>.Success(original);

        Thread.Sleep(30);
        token.ThrowIfCancellationRequested();
        var sent = HidD_SetFeature(device, edit.ApplyRequest, (uint)edit.ApplyRequest.Length);
        // Once the SET is attempted, finish the bounded verification and any safe restoration on this handle.
        var observed = ReadBackMouseDpi(device, edit, restoring: false);
        if (edit.IsApplied(observed) && MchoseK7Reports.DecodePerformance(observed, firmware) is { } confirmed)
            return Result<MousePerformanceReadout>.Success(confirmed);
        if (edit.IsOriginal(observed))
            return DpiWriteFailure(ErrorCodes.Unavailable, sent ? "鼠标未采用新的 DPI，已确认原设置。" : "DPI 写入失败，已确认原设置。");

        if (edit.CreateRestoreRequest(observed) is { } restore)
        {
            Thread.Sleep(30);
            _ = HidD_SetFeature(device, restore, (uint)restore.Length);
            var restored = ReadBackMouseDpi(device, edit, restoring: true);
            if (edit.IsOriginal(restored))
                return DpiWriteFailure(ErrorCodes.Unavailable, "DPI 写入未通过校验，已回读确认原设置恢复。");
        }

        return DpiWriteFailure(ErrorCodes.Unavailable, "暂无法确认 DPI 写入结果，请刷新设备检查当前设置。");
    }

    private static SafeFileHandle OpenMouseReceiver(string path) =>
        CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);

    private static bool IsMouseReceiver(SafeFileHandle device)
    {
        var ids = new HidAttributes { Size = 12 };
        if (device.IsInvalid || !HidD_GetAttributes(device, ref ids) || ReadCapabilities(device) is not { } caps ||
            !MchoseK7Reports.MatchesDescriptor(ids.VendorId, ids.ProductId,
                caps.UsagePage, caps.Usage, caps.FeatureReportLength))
            return false;

        var product = new byte[512];
        return HidD_GetProductString(device, product, (uint)product.Length) &&
            string.Equals(ReadText(product), MchoseK7Reports.ReceiverName, StringComparison.OrdinalIgnoreCase);
    }

    private static MousePeripheralReadout? ReadMouseIdentity(SafeFileHandle device, CancellationToken token)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var response = ReadFeatureReply(device, MchoseK7Reports.IdentityRequest(), token);
            if (MchoseK7Reports.DecodeIdentity(response) is { } mouse)
                return mouse;
            if (!MchoseK7Reports.IsEmptyIdentity(response) || attempt == 3)
                break;
            Thread.Sleep(120);
        }
        return null;
    }

    private static string? ReadMouseFirmware(SafeFileHandle device, CancellationToken token)
    {
        // The K7 needs additional time between feature-report exchanges.
        Thread.Sleep(30);
        return MchoseK7Reports.DecodeFirmware(ReadFeatureReply(device, MchoseK7Reports.FirmwareRequest(), token));
    }

    private static byte[]? ReadMouseConfiguration(SafeFileHandle device, CancellationToken token)
    {
        Thread.Sleep(30);
        var request = MchoseK7Reports.PerformanceRequest();
        var response = ReadFeatureReply(device, request, token);
        if (MchoseK7Reports.IsEmptyPerformance(response))
        {
            Thread.Sleep(120);
            response = ReadFeatureReply(device, request, token);
        }
        return response;
    }

    private static byte[]? ReadBackMouseDpi(SafeFileHandle device, MchoseK7Reports.DpiEdit edit, bool restoring)
    {
        byte[]? observed = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(120 + 60 * attempt);
            observed = ReadMouseConfiguration(device, CancellationToken.None);
            if (restoring ? edit.IsOriginal(observed) : edit.IsApplied(observed))
                break;
        }
        return observed;
    }

    private static Result<MousePerformanceReadout> DpiWriteFailure(string code, string message) =>
        Result<MousePerformanceReadout>.Failure(new SysBoxError(code, message));
}
