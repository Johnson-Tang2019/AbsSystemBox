// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public enum CleanupProcessScope
{
    LegacyAllowList, ProtectedBackground
}

public sealed record CleanupPolicy(bool Enabled = false, bool Threshold = true, bool Periodic = false, int ThresholdPercent = 90,
    int HoldSeconds = 45, int PeriodMinutes = 30, int CooldownSeconds = 300, int MinimumWorkingSetMb = 256, int MaximumTargets = 2,
    string[]? AllowedProcesses = null, CleanupProcessScope ProcessScope = CleanupProcessScope.LegacyAllowList, string[]? ExcludedProcesses = null)
{
    // Missing scope in older stores must retain the narrower allow-list behavior.
    public bool AllowsProcess(string name) => ProcessScope switch
    {
        CleanupProcessScope.LegacyAllowList => AllowedProcesses?.Contains(name, StringComparer.OrdinalIgnoreCase) == true,
        CleanupProcessScope.ProtectedBackground => !string.IsNullOrWhiteSpace(name) && ExcludedProcesses?.Contains(name, StringComparer.OrdinalIgnoreCase) != true,
        _ => false
    };
}
public sealed record CleanupAttempt(uint ProcessId, string Name, DateTimeOffset TimestampUtc, ulong? BeforeBytes, ulong? AfterBytes, bool Success, string Reason);
public sealed record CleanupState(CleanupPolicy Policy, string Status, IReadOnlyList<CleanupAttempt> Recent);
public interface IWorkingSetTrimmer
{
    IReadOnlyList<CleanupAttempt> TrimAllowed(CleanupPolicy policy, CancellationToken token);
}
public interface IRuleObservation
{
    string Status
    {
        get;
    }
    string Notification
    {
        get;
    }
    IReadOnlyList<RuleSample> Samples
    {
        get;
    }
}
