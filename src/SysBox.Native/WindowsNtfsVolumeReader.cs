// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

// Isolated lab adapter. Never installed as a privileged broker or injected into normal UI.
public sealed class WindowsNtfsVolumeReader : INtfsVolumeReader
{
    private readonly SafeFileHandle handle;
    private const uint QueryCode = 0x000900f4, EnumerateCode = 0x000900b3, ReadCode = 0x000900bb;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputBytes, byte[] output, uint outputBytes, out uint returnedBytes, IntPtr overlapped);

    public WindowsNtfsVolumeReader(string drive)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') throw new ArgumentException("A local drive letter is required.", nameof(drive));
        var info = new DriveInfo(drive);
        if (!info.IsReady || info.DriveType is not (DriveType.Fixed or DriveType.Removable) || info.DriveFormat != "NTFS")
            throw new SysBoxOperationException(new("NTFS_UNAVAILABLE", "卷不可用或不是受支持的本地 NTFS 卷，使用普通目录模式。"));
        handle = CreateFileW(@"\\.\" + drive.ToUpperInvariant(), 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
    }
    private byte[] Control(uint code, byte[]? input, bool allowEnd, out bool ended)
    {
        var output = new byte[65536]; ended = false;
        if (!DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), output, (uint)output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (allowEnd && error == 38) { ended = true; return []; }
            throw new Win32Exception(error);
        }
        if (returned > output.Length) throw new IOException("Native buffer length exceeds allocation.");
        return output[..checked((int)returned)];
    }
    public UsnJournal QueryJournal()
    {
        var buffer = Control(QueryCode, null, false, out _);
        if (buffer.Length < 56) throw new IOException("Truncated USN journal metadata.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
    }
    public NtfsBatch Enumerate(ulong continuation, long highUsn, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (highUsn < 0) throw new ArgumentOutOfRangeException(nameof(highUsn));
        // NTFS uses the documented V0 request; V1 enumeration is documented for ReFS/server.
        var input = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(input, continuation);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), highUsn);
        var output = Control(EnumerateCode, input, true, out var ended);
        token.ThrowIfCancellationRequested();
        return ended ? new(continuation, [], true) : UsnBufferParser.Parse(output);
    }
    public NtfsBatch ReadChanges(long startUsn, ulong journalId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (startUsn < 0) throw new ArgumentOutOfRangeException(nameof(startUsn));
        var input = new byte[40]; BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        // Timeout/BytesToWaitFor are zero: this read never waits for future journal changes.
        var output = Control(ReadCode, input, false, out _);
        token.ThrowIfCancellationRequested(); return UsnBufferParser.Parse(output);
    }
    public void Dispose() => handle.Dispose();
}
