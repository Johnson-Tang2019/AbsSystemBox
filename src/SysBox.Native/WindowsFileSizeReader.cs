// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;

namespace SysBox.Native;

public sealed class WindowsFileSizeReader : IFileSizeReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadStandard(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadAttributes(SafeFileHandle handle, int kind, out AttributeTagInfo info, uint size);

    public Result<FileSizeMetadata> Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return Failure("PLATFORM_UNAVAILABLE");
        if (!Path.IsPathFullyQualified(path)) return Failure("INVALID_ARGUMENT");
        if (Marshal.SizeOf<StandardInfo>() != 24 || Marshal.SizeOf<AttributeTagInfo>() != 8) return Failure("NATIVE_LAYOUT");
        // Read attributes only; open the reparse point itself and never request data/backup privileges.
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return Failure(Marshal.GetLastWin32Error() == 5 ? "ACCESS_DENIED" : "FILE_UNAVAILABLE");
        if (!ReadAttributes(handle, 9, out var attributes, 8) || (attributes.Attributes & 0x400) != 0) return Failure("REPARSE_OR_UNAVAILABLE");
        if (!ReadStandard(handle, 1, out var info, 24) || info.Directory != 0 || info.DeletePending != 0 ||
            info.EndOfFile < 0 || info.AllocationSize < 0 || info.NumberOfLinks == 0) return Failure("SIZE_UNAVAILABLE");
        return Result<FileSizeMetadata>.Success(new(info.EndOfFile, info.AllocationSize, info.NumberOfLinks, DateTimeOffset.UtcNow));
    }
    private static Result<FileSizeMetadata> Failure(string code) => Result<FileSizeMetadata>.Failure(new(code, "文件大小元数据不可用。"));
}
