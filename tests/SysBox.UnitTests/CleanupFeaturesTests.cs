// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Core;
namespace SysBox.UnitTests;

public sealed class CleanupFeaturesTests
{
    [Fact]
    public void CleanupIsOffByDefaultAndRequiresAllowedNames()
    {
        Assert.False(new CleanupPolicy().Enabled);
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(new(Enabled: true)));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(new(Enabled: true, AllowedProcesses: ["C:\\unsafe.exe"])));
        Assert.Throws<ArgumentException>(() => CleanupSchedule.Validate(new(MaximumTargets: 3)));
    }
    [Fact]
    public void ThresholdNeedsContinuousPressureAndCooldown()
    {
        var schedule = new CleanupSchedule();
        var policy = new CleanupPolicy(Enabled: true, HoldSeconds: 10, CooldownSeconds: 60, AllowedProcesses: ["allowed.exe"]);
        Assert.False(schedule.ShouldRun(policy, TimeSpan.Zero, 90));
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromSeconds(5), 90));
        Assert.True(schedule.ShouldRun(policy, TimeSpan.FromSeconds(10), 90));
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromSeconds(15), 90));
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromSeconds(70), 90)); // a gap resets continuous pressure
        Assert.True(schedule.ShouldRun(policy, TimeSpan.FromSeconds(80), 90));
    }
    [Fact]
    public void PeriodicCleanupStillNeedsPressureAndDoesNotRunEarly()
    {
        var schedule = new CleanupSchedule();
        var policy = new CleanupPolicy(Enabled: true, Threshold: false, Periodic: true, PeriodMinutes: 5, AllowedProcesses: ["allowed.exe"]);
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromMinutes(4), 90));
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromMinutes(5), 20));
        Assert.True(schedule.ShouldRun(policy, TimeSpan.FromMinutes(6), 70));
        Assert.False(schedule.ShouldRun(policy, TimeSpan.FromMinutes(7), 70));
    }
    [Fact]
    public void CleanupSettingsAndAuditStaySeparateFromRuleApprovals()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-Cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "rule-state.json"), "preserved");
            var supervisor = new CleanupSupervisor(root, new NoMemory(), new NoTrim(), () => false);
            supervisor.Configure(new(Enabled: true, AllowedProcesses: ["allowed.exe"]));
            Assert.Contains("没有活动桌面", supervisor.Read().Status);
            Assert.Equal("preserved", File.ReadAllText(Path.Combine(root, "rule-state.json")));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class NoMemory : IMemoryMonitor
    {
        public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class NoTrim : IWorkingSetTrimmer
    {
        public IReadOnlyList<CleanupAttempt> TrimAllowed(CleanupPolicy policy, CancellationToken token) => throw new NotSupportedException();
    }
}
