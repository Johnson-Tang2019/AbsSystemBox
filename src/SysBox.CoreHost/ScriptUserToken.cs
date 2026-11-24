// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SysBox.CoreHost;

internal static class ScriptUserToken
{
    public static SafeAccessTokenHandle? RestrictIfElevated(WindowsIdentity current)
    {
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            return null;
        using var original = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query |
            TokenAccessLevels.AdjustDefault | TokenAccessLevels.AssignPrimary);
        var administrators = PinSid(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        var mediumSid = new SecurityIdentifier("S-1-16-8192");
        var medium = PinSid(mediumSid);
        try
        {
            var disabled = new SidAttributes { Sid = administrators.AddrOfPinnedObject() };
            // Preserve the local account and session while applying LUA_TOKEN and disabling additional privileges.
            if (!CreateRestrictedToken(original.AccessToken, 5, 1, in disabled, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为脚本创建普通权限令牌。");
            try
            {
                var label = new SidAttributes { Sid = medium.AddrOfPinnedObject(), Attributes = 0x20 }; // SE_GROUP_INTEGRITY
                var labelSize = checked((uint)(Marshal.SizeOf<SidAttributes>() + mediumSid.BinaryLength));
                if (!SetTokenInformation(restricted, 25, in label, labelSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将脚本令牌设置为普通完整性。");
                using var child = new WindowsIdentity(restricted.DangerousGetHandle());
                if (!Equals(child.User, current.User) || child.IsSystem || new WindowsPrincipal(child).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("无法验证脚本令牌为当前普通用户。");
                return restricted;
            }
            catch { restricted.Dispose(); throw; }
        }
        finally
        {
            medium.Free();
            administrators.Free();
        }
    }

    private static GCHandle PinSid(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return GCHandle.Alloc(bytes, GCHandleType.Pinned);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAttributes { public IntPtr Sid; public uint Attributes; }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disabledCount, in SidAttributes disabled,
        uint deletedCount, IntPtr deleted, uint restrictedCount, IntPtr restrict, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int type, in SidAttributes label, uint length);
}
