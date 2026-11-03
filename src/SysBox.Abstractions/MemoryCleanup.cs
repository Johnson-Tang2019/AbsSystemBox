// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum MemoryCleanupArea
{
    WorkingSets, ProcessWorkingSets, SystemFileCache, ModifiedFileCache, ModifiedPages, StandbyPriorityZero, Standby, CombineMemory, RegistryCache
}
public sealed record MemoryCleanupAreaResult(MemoryCleanupArea Area, bool Success, string Status);
public sealed record MemoryCleanupReport(bool Cancelled, MemoryCleanupAreaResult[] Results, long? AvailableChangeBytes);
public static class MemoryCleanupCatalog
{
    public static readonly MemoryCleanupArea[] Order = Enum.GetValues<MemoryCleanupArea>();
    public static string Name(MemoryCleanupArea area) => area switch
    {
        MemoryCleanupArea.WorkingSets => "全局工作集",
        MemoryCleanupArea.ProcessWorkingSets => "逐进程工作集（保护前台及系统进程）",
        MemoryCleanupArea.SystemFileCache => "系统文件缓存",
        MemoryCleanupArea.ModifiedFileCache => "修改的文件缓存",
        MemoryCleanupArea.ModifiedPages => "修改的页面列表",
        MemoryCleanupArea.StandbyPriorityZero => "优先级 0 待机列表",
        MemoryCleanupArea.Standby => "完整待机列表（可能短暂停顿）",
        MemoryCleanupArea.CombineMemory => "内存合并",
        MemoryCleanupArea.RegistryCache => "注册表缓存",
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
    public static MemoryCleanupArea[] Validate(IEnumerable<MemoryCleanupArea> areas)
    {
        var chosen = areas.ToArray();
        if (chosen.Length is < 1 or > 9 || chosen.Any(a => !Enum.IsDefined(a)) || chosen.Distinct().Count() != chosen.Length)
            throw new ArgumentException("请选择 1–9 个有效且不重复的清理项目。");
        return Order.Where(chosen.Contains).ToArray();
    }
    public static bool IsDefault(MemoryCleanupArea area) => area is not (MemoryCleanupArea.ProcessWorkingSets or MemoryCleanupArea.ModifiedPages or MemoryCleanupArea.Standby);
    // Observed operation combination from PCL-CE 2.10.4; no upstream implementation is copied.
    public static bool IsPclCombination(MemoryCleanupArea area) => area is MemoryCleanupArea.WorkingSets or MemoryCleanupArea.ModifiedFileCache or
        MemoryCleanupArea.ModifiedPages or MemoryCleanupArea.StandbyPriorityZero or MemoryCleanupArea.Standby or MemoryCleanupArea.RegistryCache or MemoryCleanupArea.CombineMemory;
}
