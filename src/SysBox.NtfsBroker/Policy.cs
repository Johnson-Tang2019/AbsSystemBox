// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SysBox.Abstractions;
namespace SysBox.NtfsBroker;

public sealed record VolumeGrant(string Sid, string Image, string[] Volumes);
public sealed record BrokerPolicy(int Version, VolumeGrant[] Grants)
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AbsSystemBox", "NtfsService");
    public static string ServiceImage => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "NtfsService", "SysBox.NtfsService.exe");
    public static BrokerPolicy Load()
    {
        var path = Path.Combine(DirectoryPath, "grants.json");
        VerifyProtected(path);
        if (new FileInfo(path).Length > 65536)
            throw new IOException("授权配置过大。");
        var policy = JsonSerializer.Deserialize<BrokerPolicy>(File.ReadAllText(path)) ?? throw new IOException("授权配置损坏。");
        policy.Validate();
        return policy;
    }
    public void Validate()
    {
        if (Version != 1 || Grants is null || Grants.Length > 32)
            throw new IOException("授权配置版本或数量无效。");
        foreach (var grant in Grants)
        {
            _ = new SecurityIdentifier(grant.Sid);
            if (!Path.IsPathFullyQualified(grant.Image) || Path.GetFileName(grant.Image) != "SysBox.CoreHost.exe" || grant.Volumes.Length is < 1 or > 26)
                throw new IOException("授权目标无效。");
            foreach (var volume in grant.Volumes)
                NtfsIdentity.Volume(volume);
        }
    }
    public string[] Allowed(string sid, string image) => Grants.Where(g => g.Sid == sid && string.Equals(Path.GetFullPath(g.Image), Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase)).SelectMany(g => g.Volumes).Select(NtfsIdentity.Volume).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static void VerifyProtected(string path)
    {
        for (var item = new FileInfo(path) as FileSystemInfo; item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("授权/服务路径不能经过重解析点。");
            var security = item is FileInfo f ? (FileSystemSecurity)f.GetAccessControl() : ((DirectoryInfo)item).GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw new UnauthorizedAccessException("授权路径所有者缺失。");
            if (!owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && owner.Value != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")
                throw new UnauthorizedAccessException("授权路径所有者不可信。");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                    continue;
                var principal = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Allow && !principal.IsWellKnown(WellKnownSidType.LocalSystemSid) && !principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && principal.Value != "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" &&
                    (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles)) != 0)
                    throw new UnauthorizedAccessException("普通用户可修改授权或服务路径。");
            }
            // ProgramData itself allows creating unrelated child folders; the product directory is the trust root.
            if (string.Equals(item.FullName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AbsSystemBox"), StringComparison.OrdinalIgnoreCase) || string.Equals(item.FullName, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
                break;
        }
    }
}
