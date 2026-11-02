// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record CleanupPolicy(bool Enabled = false, bool Threshold = true, bool Periodic = false, int ThresholdPercent = 85,
    int HoldSeconds = 45, int PeriodMinutes = 30, int CooldownSeconds = 300, int MinimumWorkingSetMb = 256, int MaximumTargets = 2, string[]? AllowedProcesses = null);
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
