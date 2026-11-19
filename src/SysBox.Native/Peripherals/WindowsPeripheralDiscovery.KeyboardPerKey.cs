// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed partial class WindowsPeripheralDiscovery
{
    private static Result<KeyboardPerKeyReadout> WriteKeyboardKeyColor(string path, string expectedRevision,
        KeyboardKeyColorChange change, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var device = CreateFile(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!IsKeyboardDescriptor(device))
            return OperationFailure<KeyboardPerKeyReadout>("无法打开键盘配置接口。请确认有线连接并关闭其他配置程序。");
        var identity = ReadKeyboardIdentity(device, token);
        if (KeyboardIdentityReport.Model(identity) is null ||
            ReadKeyboardRgbState(device, token) is not { } original || original.Describe() is not { } current)
            return OperationFailure<KeyboardPerKeyReadout>("无法核对有线 F87 Pro 的原始逐键灯光配置。");
        if (current.EditRevision is null || !string.Equals(expectedRevision, current.EditRevision, StringComparison.Ordinal))
            return OperationFailure<KeyboardPerKeyReadout>("逐键灯光配置已改变或不可编辑，请刷新后重试。");
        if (KeyboardPerKeyRgbReport.ApplyColor(original.Bank, change.Key, change.ColorRgb) is not { } bank)
            return Result<KeyboardPerKeyReadout>.Failure(new SysBoxError(ErrorCodes.InvalidArgument,
                "所选按键或 RGB 值无法应用到此灯光配置。"));

        var configuration = change.EnableCustomMode && !current.IsActive
            ? KeyboardPerKeyRgbReport.EnableCustomMode(original.Configuration) : original.Configuration;
        var desired = new KeyboardRgbState(bank, configuration);
        if (original.Matches(desired))
            return Result<KeyboardPerKeyReadout>.Success(current);

        token.ThrowIfCancellationRequested();
        if (!original.Bank.AsSpan().SequenceEqual(desired.Bank))
            SendKeyboardRgbBank(device, desired.Bank);
        // Verify the whole color bank before switching the board to display it.
        if (!original.Configuration.AsSpan().SequenceEqual(desired.Configuration) &&
            ReadKeyboardRgbState(device, CancellationToken.None) is { } stored &&
            stored.Bank.AsSpan().SequenceEqual(desired.Bank) && stored.Configuration.AsSpan().SequenceEqual(original.Configuration))
            SendKeyboardConfiguration(device, desired.Configuration);

        // Finish bounded readback or restoration even if the caller cancels after issuing a write.
        var observed = ReadKeyboardRgbState(device, CancellationToken.None);
        if (observed is not null && observed.Matches(desired) && observed.Describe() is { } confirmed)
            return Result<KeyboardPerKeyReadout>.Success(confirmed);
        if (observed is not null && observed.Matches(original))
            return OperationFailure<KeyboardPerKeyReadout>("逐键灯光设置未应用，原设置已核对。");
        if (observed is not null && observed.ContainsOnly(original, desired) &&
            string.Equals(ReadKeyboardIdentity(device, CancellationToken.None), identity, StringComparison.Ordinal) &&
            ReadKeyboardRgbState(device, CancellationToken.None) is { } latest && latest.ContainsOnly(original, desired))
        {
            if (latest.Matches(desired) && latest.Describe() is { } delayed)
                return Result<KeyboardPerKeyReadout>.Success(delayed);
            if (!latest.Configuration.AsSpan().SequenceEqual(original.Configuration))
                SendKeyboardConfiguration(device, original.Configuration);
            if (!latest.Bank.AsSpan().SequenceEqual(original.Bank))
                SendKeyboardRgbBank(device, original.Bank);
            if (ReadKeyboardRgbState(device, CancellationToken.None) is { } restored && restored.Matches(original))
                return OperationFailure<KeyboardPerKeyReadout>("逐键灯光设置未通过核验，原设置已恢复并回读。");
        }

        return OperationFailure<KeyboardPerKeyReadout>("无法确认逐键灯光设置结果。请先刷新核对键盘当前配置。");
    }

    private static KeyboardRgbState? ReadKeyboardRgbState(SafeFileHandle device, CancellationToken token) =>
        ReadKeyboardConfiguration(device, token) is { } configuration &&
        KeyboardPerKeyRgbReport.ReadPayload(ReadFeatureReply(device, KeyboardPerKeyRgbReport.CreateRequest(), token)) is { } payload
            ? new(payload, configuration) : null;

    private static void SendKeyboardRgbBank(SafeFileHandle device, byte[] bank)
    {
        var report = KeyboardPerKeyRgbReport.CreateWriteRequest(bank);
        HidD_SetFeature(device, report, (uint)report.Length);
        Thread.Sleep(40);
    }

    private sealed record KeyboardRgbState(byte[] Bank, byte[] Configuration)
    {
        internal KeyboardPerKeyReadout? Describe() => KeyboardPerKeyRgbReport.Describe(Bank, Configuration);

        internal bool Matches(KeyboardRgbState other) =>
            Bank.AsSpan().SequenceEqual(other.Bank) && Configuration.AsSpan().SequenceEqual(other.Configuration);

        internal bool ContainsOnly(KeyboardRgbState original, KeyboardRgbState desired) =>
            (Bank.AsSpan().SequenceEqual(original.Bank) || Bank.AsSpan().SequenceEqual(desired.Bank)) &&
            (Configuration.AsSpan().SequenceEqual(original.Configuration) || Configuration.AsSpan().SequenceEqual(desired.Configuration));
    }
}
