// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Native;
using SysBox.Presentation;

namespace SysBox.UnitTests;

public sealed class CleanupProtectionRevisionTests
{
    [Fact]
    public void ProtectedModeAcceptsEmptyExclusionsAndRejectsInvalidNamesAndScopes()
    {
        var policy = new CleanupPolicy(Enabled: true, ProcessScope: CleanupProcessScope.ProtectedBackground);
        CleanupSchedule.Validate(policy);
        Assert.True(policy.AllowsProcess("browser.exe"));
        Assert.False((policy with
        {
            ExcludedProcesses = ["Browser.EXE"]
        }).AllowsProcess("browser.exe"));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(policy with { ExcludedProcesses = ["C:\\browser.exe"] }));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(policy with { ExcludedProcesses = [null!] }));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(policy with { ProcessScope = (CleanupProcessScope)99 }));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(policy with { Threshold = false, Periodic = false }));
        Assert.True(WorkingSetAccess.IsProtectedName("JAVAw.EXE"));
        Assert.True(WorkingSetAccess.IsProtectedName("audiodg.exe"));
        Assert.True(WorkingSetAccess.IsProtectedName("SysBox.App.exe"));
    }

    [Fact]
    public void OldPersistedAllowListNeverExpandsToAllBackgroundProcesses()
    {
        var policy = JsonSerializer.Deserialize<CleanupPolicy>("{\"Enabled\":true,\"AllowedProcesses\":[\"old.exe\"]}")!;
        CleanupSchedule.Validate(policy);
        Assert.Equal(CleanupProcessScope.LegacyAllowList, policy.ProcessScope);
        Assert.True(policy.AllowsProcess("OLD.EXE"));
        Assert.False(policy.AllowsProcess("new.exe"));
        Assert.False((policy with
        {
            ProcessScope = (CleanupProcessScope)99
        }).AllowsProcess("old.exe"));
        var restored = JsonSerializer.Deserialize<CleanupPolicy>(JsonSerializer.Serialize(policy))!;
        Assert.False(restored.AllowsProcess("new.exe"));
    }

    [Fact]
    public void PclCombinationSelectsSevenExistingOperationsWithoutChangingDefaultOrExecuting()
    {
        var areas = MemoryCleanupCatalog.Order.Where(MemoryCleanupCatalog.IsPclCombination).ToArray();
        Assert.Equal(7, areas.Length);
        Assert.Contains(MemoryCleanupArea.Standby, areas);
        Assert.DoesNotContain(MemoryCleanupArea.ProcessWorkingSets, areas);
        Assert.DoesNotContain(MemoryCleanupArea.SystemFileCache, areas);
        Assert.False(MemoryCleanupCatalog.IsDefault(MemoryCleanupArea.Standby));
        Assert.Equal(areas, MemoryCleanupCatalog.Validate(areas));
    }

    [Fact]
    public void StorageSizesUseDecimalUnitsWithoutLosingLargeIntegerOrPartialCoverageMeaning()
    {
        Assert.Equal("999 B", SizeFormatting.Format(999));
        Assert.EndsWith(" KB", SizeFormatting.Format(1000));
        Assert.EndsWith(" MB", SizeFormatting.Format(1_000_000));
        Assert.EndsWith(" GB", SizeFormatting.Format(5_156_261_846));
        Assert.EndsWith(" GB", SizeFormatting.Format(long.MaxValue));
        Assert.StartsWith("-", SizeFormatting.Format(long.MinValue));
        var row = new StorageRow(new("fixture", "fixture", true, 5_156_261_846, 5_253_654_712, 1, 2, 1, 0, DataCompleteness.Partial, DateTimeOffset.UtcNow));
        Assert.Contains("已知部分", row.Detail);
        Assert.Contains("GB 逻辑大小", row.Detail);
        Assert.Contains("GB 已知分配大小", row.Detail);
        Assert.Contains("1 个未知", row.Detail);
        Assert.DoesNotContain("5,156,261,846", row.Detail);
    }
}
