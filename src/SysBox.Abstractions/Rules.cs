// SPDX-License-Identifier: GPL-3.0-or-later
namespace SysBox.Abstractions;

public sealed record RuleIssue(string Code, string Path, string Message);
public sealed record RuleValidation(MemoryRule? Rule, IReadOnlyList<RuleIssue> Issues)
{
    public bool IsValid => Rule is not null && Issues.Count == 0;
}
public sealed record MemoryRule(int SchemaVersion, string Id, string Name, long Revision, bool Enabled,
    RuleSampling Sampling, RuleConditions Conditions, RuleCooldown Cooldown, RuleProtections Protections,
    RuleAction[] Actions, RuleSimulation Simulation, RuleObservability Observability);
public sealed record RuleSampling(int IntervalSeconds, int KeepHistoryMinutes);
public sealed record RuleConditions(RuleCondition[]? All, RuleCondition[]? Any);
public sealed record RuleCondition(string Metric, string Op, double Value, int HoldSeconds);
public sealed record RuleCooldown(int AfterActionSeconds, int MaxActionsPerHour);
public sealed record RuleProtections(bool ExcludeForeground, bool ExcludeSystemProcesses, string[] ExcludeProcessNames, int MinProcessAgeSeconds);
public sealed record RuleAction(string Type, bool Enabled = true, string? Level = null, string? Message = null,
    RuleSelector? Selector = null, bool RequireInteractiveApproval = true);
public sealed record RuleSelector(bool OnlyUserProcesses, int MinWorkingSetMb, int MaxTargets);
public sealed record RuleSimulation(bool DefaultDryRun);
public sealed record RuleObservability(bool AuditEnabled, bool IncludeRawPathsInLogs);

// Elapsed is measured by a monotonic clock; wall time is display-only.
public sealed record RuleSample(TimeSpan Elapsed, MemorySnapshot Snapshot);
public sealed record RuleDecision(DateTimeOffset TimestampUtc, string State, string Reason, string[] Notifications);
public sealed record RuleReport(string YamlSha256, long Revision, IReadOnlyList<RuleDecision> Decisions,
    int NotificationCount, int ActuallyExecuted = 0);
public sealed record RuleVersion(long Revision, string Yaml, string Sha256, DateTimeOffset CreatedUtc, string Summary);
public sealed record RuleProposal(string Id, long BaseRevision, string BaseSha256, string Yaml, string Sha256,
    string Reason, DateTimeOffset ExpiresUtc, RuleReport Simulation, string Status = "Pending");
public sealed record RuleAudit(DateTimeOffset TimestampUtc, string ProposalId, string Outcome, long Revision, string Summary);
public sealed record RuleState(RuleVersion Active, RuleVersion[] Versions, RuleProposal[] Proposals, RuleAudit[] Audit);

// Agent-facing surface deliberately has no apply/approve/rollback operation.
public interface IRuleService
{
    RuleValidation Validate(string yaml);
    RuleReport Simulate(string yaml, IReadOnlyList<RuleSample> samples);
    RuleState Read();
    RuleProposal Propose(string yaml, long expectedRevision, string expectedHash, string reason, IReadOnlyList<RuleSample> samples);
}
public sealed class RuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
