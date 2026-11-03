// SPDX-License-Identifier: GPL-3.0-or-later
// Native operations/order adapted from Fluent Reduct, commit 64fa85f; see THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SysBox.Abstractions;
namespace SysBox.Native;

public static class SystemMemoryCleanup
{
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, byte[]? buffer, uint size);
    [DllImport("ntdll.dll")] private static extern int RtlAdjustPrivilege(uint privilege, byte enabled, byte thread, out byte previous);
    public static (int Class, byte[]? Payload) Payload(MemoryCleanupArea area)
    {
        var bytes = new byte[4];
        switch (area)
        {
            case MemoryCleanupArea.WorkingSets:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 2);
                return (80, bytes);
            case MemoryCleanupArea.ModifiedPages:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 3);
                return (80, bytes);
            case MemoryCleanupArea.Standby:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 4);
                return (80, bytes);
            case MemoryCleanupArea.StandbyPriorityZero:
                BinaryPrimitives.WriteInt32LittleEndian(bytes, 5);
                return (80, bytes);
            case MemoryCleanupArea.SystemFileCache:
            case MemoryCleanupArea.ModifiedFileCache:
                bytes = new byte[64];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), ulong.MaxValue);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue);
                return (area == MemoryCleanupArea.SystemFileCache ? 21 : 81, bytes);
            case MemoryCleanupArea.CombineMemory:
                return (130, new byte[24]);
            case MemoryCleanupArea.RegistryCache:
                return (155, null);
            default:
                throw new ArgumentException("该项目不是固定 NT 内存操作。");
        }
    }
    public static MemoryCleanupAreaResult[] Execute(MemoryCleanupArea[] selected, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("需要独立管理员进程。");
        var areas = MemoryCleanupCatalog.Validate(selected);
        var adjusted = new List<(uint Id, byte Previous)>();
        try
        {
            foreach (var id in new uint[] { 5, 13 })
            if (RtlAdjustPrivilege(id, 1, 0, out var old) >= 0)
                adjusted.Add((id, old));
            var results = new List<MemoryCleanupAreaResult>();
            foreach (var area in areas)
            {
                token.ThrowIfCancellationRequested();
                if (area == MemoryCleanupArea.ProcessWorkingSets)
                {
                    results.Add(TrimProcesses(token));
                    continue;
                }
                var spec = Payload(area);
                var status = NtSetSystemInformation(spec.Class, spec.Payload, (uint)(spec.Payload?.Length ?? 0));
                results.Add(new(area, status >= 0, status >= 0 ? "已执行" : $"系统拒绝或不支持：NTSTATUS 0x{unchecked((uint)status):X8}"));
            }
            return results.ToArray();
        }
        finally { foreach (var entry in adjusted.AsEnumerable().Reverse()) RtlAdjustPrivilege(entry.Id, entry.Previous, 0, out _); }
    }
    private static MemoryCleanupAreaResult TrimProcesses(CancellationToken token)
    {
        var api = new WindowsMemoryApi();
        var batch = api.EnumerateProcessIds(token);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var success = 0;
        var skipped = 0;
        foreach (var pid in batch.Ids)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(3))
                break;
            try
            {
                var p = api.ReadProcess(pid);
                if (p.StartTimeUtc is null || p.ImagePath is null)
                {
                    skipped++;
                    continue;
                }
                var result = WorkingSetAccess.Trim(pid, p.StartTimeUtc.Value, Path.GetFileName(p.ImagePath), 64 * 1048576);
                if (result.Success)
                    success++;
                else
                    skipped++;
            }
            catch (System.ComponentModel.Win32Exception) { skipped++; }
        }
        return new(MemoryCleanupArea.ProcessWorkingSets, true, $"已收缩 {success} 个，保护/不可访问 {skipped} 个；逐进程预算 3 秒。");
    }
}
