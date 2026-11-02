// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace SysBox.NtfsBroker;

internal static class ServiceIdentity
{
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public uint Type, Start, Error; public IntPtr Image, Group; public uint Tag; public IntPtr Dependencies, Account, Name;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, byte[] buffer, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr buffer, uint size, out uint required);
    public static void Verify(uint pid, string image)
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid)
            throw new UnauthorizedAccessException();
        using var service = OpenServiceW(manager, "AbsSystemBox.Ntfs", 5);
        if (service.IsInvalid)
            throw new UnauthorizedAccessException();
        var status = new byte[36];
        if (!QueryServiceStatusEx(service, 0, status, 36, out var used) || used > 36 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(4)) != 4 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(28)) != pid)
            throw new UnauthorizedAccessException();
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var size);
        if (size < 64 || size > 65536 || Marshal.SizeOf<Configuration>() != 64)
            throw new UnauthorizedAccessException();
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!QueryServiceConfigW(service, buffer, size, out used) || used > size)
                throw new UnauthorizedAccessException();
            var configuration = Marshal.PtrToStructure<Configuration>(buffer);
            string Read(IntPtr pointer)
            {
                var offset = pointer.ToInt64() - buffer.ToInt64();
                if (offset < 64 || offset >= size || offset % 2 != 0)
                    throw new UnauthorizedAccessException();
                var chars = new List<char>();
                for (var i = offset; i + 1 < size; i += 2)
                {
                    var c = (char)Marshal.ReadInt16(buffer, checked((int)i));
                    if (c == '\0')
                        return new string(chars.ToArray());
                    chars.Add(c);
                }
                throw new UnauthorizedAccessException();
            }
            if (configuration.Type != 16 || Read(configuration.Account) is not ("LocalSystem" or "NT AUTHORITY\\SYSTEM") || !string.Equals(Read(configuration.Image), '"' + BrokerPolicy.ServiceImage + '"', StringComparison.OrdinalIgnoreCase) || !string.Equals(image, BrokerPolicy.ServiceImage, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
