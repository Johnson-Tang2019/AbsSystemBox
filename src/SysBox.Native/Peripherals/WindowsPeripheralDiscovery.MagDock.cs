// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static readonly TimeSpan MagDockReplyTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan MagDockWriteTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MagDockWriteSettle = TimeSpan.FromMilliseconds(400);

    private static MagDockLightingReadout? ReadMagDock(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = OpenMagDockHandle(path);
        using var stream = OpenMagDockStream(handle);
        return stream is null ? null : ReadMagDockBlock(handle, stream, token)?.Describe();
    }

    private static Result<MagDockLightingReadout> WriteMagDockLighting(string path, string expectedRevision,
        MagDockLightingChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var handle = OpenMagDockHandle(path);
        using var stream = OpenMagDockStream(handle);
        if (stream is null || ReadMagDockBlock(handle, stream, token) is not { } original)
            return OperationFailure<MagDockLightingReadout>("暂未读取到底座原始灯效，请连接底座后刷新。");

        var current = original.Describe();
        if (string.IsNullOrEmpty(current.EditRevision) ||
            !string.Equals(current.EditRevision, expectedRevision, StringComparison.Ordinal))
            return OperationFailure<MagDockLightingReadout>("底座灯效已变化或暂不可编辑，请刷新后重试。");
        if (original.Apply(change) is not { } desired || desired.CreateWriteRequest() is not { } request ||
            original.CreateWriteRequest() is not { } restore)
            return Result<MagDockLightingReadout>.Failure(new SysBoxError(ErrorCodes.InvalidArgument,
                "灯效设置不适用于当前模式，请检查模式、颜色、速度和亮度。"));
        if (original.SameSettings(desired))
            return Result<MagDockLightingReadout>.Success(current);

        token.ThrowIfCancellationRequested();
        // Once the output report is attempted, finish verification and safe recovery on this handle.
        SendMagDockChange(stream, request);
        var observed = ReadMagDockBlock(handle, stream, CancellationToken.None);
        if (observed is not null && observed.SameSettings(desired))
            return Result<MagDockLightingReadout>.Success(observed.Describe());
        if (observed is not null && observed.SameSettings(original))
            return OperationFailure<MagDockLightingReadout>("底座未采用新灯效，已回读确认原设置。");
        if (observed is not null && original.CanRestoreAfter(desired, observed))
        {
            SendMagDockChange(stream, restore);
            var restored = ReadMagDockBlock(handle, stream, CancellationToken.None);
            if (restored is not null && restored.SameSettings(original))
                return OperationFailure<MagDockLightingReadout>("底座灯效未通过校验，已回读确认原设置恢复。");
        }

        return OperationFailure<MagDockLightingReadout>("暂无法确认底座灯效写入结果，请刷新检查当前设置。");
    }

    private static SafeFileHandle OpenMagDockHandle(string path) =>
        CreateFile(path, 0xc0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);

    private static FileStream? OpenMagDockStream(SafeFileHandle handle)
    {
        var attributes = new HidAttributes { Size = 12 };
        if (handle.IsInvalid || !HidD_GetAttributes(handle, ref attributes) ||
            ReadCapabilities(handle) is not { } caps ||
            !MagDockLightingReport.Matches(attributes.VendorId, attributes.ProductId, caps.UsagePage, caps.Usage,
                caps.InputReportLength, caps.OutputReportLength))
            return null;

        try
        {
            // A one-byte FileStream buffer disables managed buffering. Each write is one HID report.
            return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static MagDockLightingReport.Snapshot? ReadMagDockBlock(SafeFileHandle handle, FileStream stream,
        CancellationToken token)
    {
        var request = MagDockLightingReport.CreateReadRequest();
        var input = new byte[MagDockLightingReport.ReportLength];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // Discard queued acknowledgements and old read replies before requesting a fresh report.
            if (!HidD_FlushQueue(handle))
                return null;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(MagDockReplyTimeout);
            try
            {
                stream.WriteAsync(request.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
                while (!deadline.IsCancellationRequested)
                {
                    var length = stream.ReadAsync(input.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
                    if (length == 0)
                        break;
                    if (length == input.Length && MagDockLightingReport.Decode(input) is { } state)
                        return state;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // A missing reply may be retried within the operation budget.
            }
            catch (IOException)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private static void SendMagDockChange(FileStream stream, byte[] request)
    {
        using var deadline = new CancellationTokenSource(MagDockWriteTimeout);
        try
        {
            stream.WriteAsync(request.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            // The device could apply the request before the host reports an interrupted write.
        }
        Thread.Sleep(MagDockWriteSettle);
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FlushQueue(SafeFileHandle device);
}
