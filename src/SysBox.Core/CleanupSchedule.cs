// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using SysBox.Abstractions;
namespace SysBox.Core;

public sealed class CleanupSchedule
{
    private TimeSpan? pressureStart, lastRun, lastSample; private readonly Queue<TimeSpan> runs = new();
    public static void Validate(CleanupPolicy policy)
    {
        if (policy.ThresholdPercent is < 50 or > 98 || policy.HoldSeconds is < 10 or > 3600 || policy.PeriodMinutes is < 5 or > 1440 ||
           policy.CooldownSeconds is < 60 or > 86400 || policy.MinimumWorkingSetMb is < 64 or > 65536 || policy.MaximumTargets is < 1 or > 2 ||
           (policy.AllowedProcesses?.Length ?? 0) > 32 || policy.AllowedProcesses?.Any(n => !Regex.IsMatch(n, @"^[\p{L}\p{N}_.-]{1,64}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) == true)
            throw new ArgumentException("清理参数超出范围或进程名无效。");
        if (policy.Enabled && (!policy.Threshold && !policy.Periodic || policy.AllowedProcesses is not { Length: > 0 }))
            throw new ArgumentException("启用前请选择清理方式并填写允许处理的进程名。");
    }
    public bool ShouldRun(CleanupPolicy policy, TimeSpan elapsed, double usagePercent)
    {
        Validate(policy);
        if (lastSample is { } previous && (elapsed < previous || elapsed - previous > TimeSpan.FromSeconds(15)))
            pressureStart = null;
        lastSample = elapsed;
        if (!policy.Enabled || !double.IsFinite(usagePercent) || usagePercent is < 0 or > 100)
        {
            pressureStart = null;
            return false;
        }
        if (usagePercent >= policy.ThresholdPercent)
            pressureStart ??= elapsed;
        else
            pressureStart = null;
        while (runs.TryPeek(out var oldest) && elapsed - oldest >= TimeSpan.FromHours(1))
            runs.Dequeue();
        if (runs.Count >= 6 || lastRun is { } last && elapsed - last < TimeSpan.FromSeconds(policy.CooldownSeconds))
            return false;
        var threshold = policy.Threshold && pressureStart is { } start && elapsed - start >= TimeSpan.FromSeconds(policy.HoldSeconds);
        // Periodic cleanup still requires meaningful memory pressure, never an empty-cache timer.
        var periodic = policy.Periodic && usagePercent >= 50 && elapsed - (lastRun ?? TimeSpan.Zero) >= TimeSpan.FromMinutes(policy.PeriodMinutes);
        if (!threshold && !periodic)
            return false;
        lastRun = elapsed;
        runs.Enqueue(elapsed);
        pressureStart = null;
        return true;
    }
}
