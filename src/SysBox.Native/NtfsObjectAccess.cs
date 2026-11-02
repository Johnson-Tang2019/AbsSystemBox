// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SysBox.Abstractions;
namespace SysBox.Native;

public static class NtfsObjectAccess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public uint Size, Type; public ulong Low, High;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref Descriptor descriptor, uint access, uint sharing, IntPtr security, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, byte[] buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? name, uint nameSize, out uint serial, out uint maximum, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstFileNameW(string file, uint flags, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextFileNameW(FindHandle handle, ref uint length, StringBuilder name);
    [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr handle);
    private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FindHandle(IntPtr handle) : base(true) => SetHandle(handle); protected override bool ReleaseHandle() => FindClose(handle);
    }
    public static void VerifyNtfs(SafeFileHandle handle)
    {
        var system = new StringBuilder(32);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out _, out _, out _, system, 32) || system.ToString() != "NTFS")
            throw new IOException("授权卷离线或不再是 NTFS。");
    }
    public static NtfsVolumeBinding BindRoot(string root)
    {
        root = Path.GetFullPath(root);
        var drive = new DriveInfo(root);
        if (!string.Equals(root.TrimEnd('\\'), drive.RootDirectory.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || drive.DriveFormat != "NTFS")
            throw new ArgumentException("高速索引请选择本地 NTFS 卷根目录。");
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(drive.RootDirectory.FullName, name, 64))
            throw new Win32Exception();
        using var handle = CreateFileW(drive.RootDirectory.FullName, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception();
        VerifyNtfs(handle);
        return new(NtfsIdentity.Volume(name.ToString()), drive.RootDirectory.FullName, Identity(handle));
    }
    private static byte[] Information(SafeFileHandle handle, int kind, int size)
    {
        var bytes = new byte[size];
        if (!GetFileInformationByHandleEx(handle, kind, bytes, (uint)size))
            throw new Win32Exception();
        return bytes;
    }
    private static string Identity(SafeFileHandle handle) => NtfsIdentity.FileId(Convert.ToHexString(Information(handle, 18, 24).AsSpan(8, 16)));
    public static NtfsObject Read(SafeFileHandle volume, string volumeId, string fileId, CancellationToken token)
    {
        NtfsObject Failure(string state, string error) => new(fileId, 0, null, null, 0, null, [], state, error);
        var bytes = Convert.FromHexString(fileId);
        var descriptor = new Descriptor { Size = 24, Type = bytes.Length == 8 ? 0u : 2u, Low = BinaryPrimitives.ReadUInt64LittleEndian(bytes), High = bytes.Length == 16 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)) : 0 };
        if (Marshal.SizeOf<Descriptor>() != 24)
            throw new PlatformNotSupportedException();
        using var handle = OpenFileById(volume, ref descriptor, 0x80, 7, IntPtr.Zero, 0x02200000);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return Failure(error is 2 or 3 or 1168 ? "Missing" : "Unavailable", "WIN32_" + error);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (Identity(handle) != fileId)
                return Failure("Unavailable", "IDENTITY_CHANGED");
            var basic = Information(handle, 0, 40);
            var standard = Information(handle, 1, 24);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(basic.AsSpan(32));
            var allocated = BinaryPrimitives.ReadInt64LittleEndian(standard);
            var logical = BinaryPrimitives.ReadInt64LittleEndian(standard.AsSpan(8));
            if (allocated < 0 || logical < 0)
                return Failure("Unavailable", "INVALID_SIZE");
            var path = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(handle, path, 32768, 1);
            if (length == 0 || length >= 32768 || !path.ToString().StartsWith(volumeId, StringComparison.OrdinalIgnoreCase))
                return Failure("Unavailable", "PATH_UNAVAILABLE");
            var relative = NtfsIdentity.Relative(@"\" + path.ToString()[volumeId.Length..]);
            var links = new List<string>();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(standard.AsSpan(16));
            if ((attributes & 0x10) != 0 || count <= 1 || (attributes & 0x400) != 0)
                links.Add(relative);
            else
            {
                uint capacity = 32768;
                var buffer = new StringBuilder(32768);
                using var search = new FindHandle(FindFirstFileNameW(path.ToString(), 0, ref capacity, buffer));
                if (search.IsInvalid)
                    return Failure("Unavailable", "LINK_ENUMERATION_FAILED");
                var chars = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var link = NtfsIdentity.Relative(buffer.ToString());
                    chars = checked(chars + link.Length);
                    if (links.Count >= 1024 || chars > 65536)
                        return Failure("Unavailable", "LINK_LIMIT");
                    links.Add(link);
                    capacity = 32768;
                    buffer.Clear();
                    if (FindNextFileNameW(search, ref capacity, buffer))
                        continue;
                    if (Marshal.GetLastWin32Error() != 38)
                        return Failure("Unavailable", "LINK_ENUMERATION_FAILED");
                    break;
                }
                if (links.Distinct(StringComparer.Ordinal).Count() != count)
                    return Failure("Unavailable", "LINKS_CHANGED");
            }
            var write = BinaryPrimitives.ReadInt64LittleEndian(basic.AsSpan(16));
            return new(fileId, attributes, logical, allocated, count, new DateTimeOffset(DateTime.FromFileTimeUtc(write)), links.Distinct(StringComparer.Ordinal).ToArray(), "Ready");
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or IOException) { return Failure("Unavailable", "METADATA_UNAVAILABLE"); }
    }
}
