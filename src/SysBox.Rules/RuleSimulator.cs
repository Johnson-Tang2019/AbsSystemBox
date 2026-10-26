// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using SysBox.Abstractions;

namespace SysBox.Rules;

public static class RuleSimulator
{
    public static string Hash(string yaml) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)));
    public static RuleReport Replay(string yaml, MemoryRule rule, IReadOnlyList<RuleSample> samples)
    {
        if (samples.Count > 1024) throw new RuleException("HISTORY_LIMIT", "历史样本不得超过 1024 项。");
        var engine = new RuleEngine(rule);
        var decisions = samples.Select(engine.Observe).ToArray();
        return new(Hash(yaml), rule.Revision, decisions, decisions.Sum(d => d.Notifications.Length));
    }
}

// Pure state machine: it emits notification descriptions and never executes an action.
public sealed class RuleEngine(MemoryRule rule)
{
    private readonly RuleCondition[] conditions = rule.Conditions.All ?? rule.Conditions.Any!;
    private readonly Dictionary<int,TimeSpan> starts = [];
    private readonly Queue<TimeSpan> notifications = [];
    private TimeSpan? previous;
    private TimeSpan? lastNotification;
    public RuleDecision Observe(RuleSample sample)
    {
        RuleDecision Result(string state, string reason, string[]? messages = null) => new(sample.Snapshot.TimestampUtc,state,reason,messages ?? []);
        if (!rule.Enabled) return Result("Disabled", "规则已禁用。");
        var discontinuity = previous is {} prev && (sample.Elapsed <= prev || sample.Elapsed - prev > TimeSpan.FromSeconds(rule.Sampling.IntervalSeconds * 2));
        if (discontinuity) starts.Clear();
        // Backward monotonic time invalidates this session rather than bypassing cooldown.
        if (previous is {} last && sample.Elapsed <= last) return Result("InvalidTime", "单调时间倒退或重复，拒绝该样本。");
        previous = sample.Elapsed;
        var snapshot = sample.Snapshot;
        var matched = new bool[conditions.Length];
        var ready = new bool[conditions.Length];
        for (var i=0;i<conditions.Length;i++)
        {
            var c = conditions[i];
            double? value = c.Metric == "available_memory_percent"
                ? snapshot.TotalPhysicalBytes > 0 && snapshot.AvailablePhysicalBytes <= snapshot.TotalPhysicalBytes ? 100d * snapshot.AvailablePhysicalBytes / snapshot.TotalPhysicalBytes : null
                : snapshot.CommitLimitBytes is > 0 && snapshot.CommitTotalBytes is {} total && total <= snapshot.CommitLimitBytes
                    ? 100d * total / snapshot.CommitLimitBytes.Value : null;
            matched[i] = value is {} v && c.Op switch { "lt" => v < c.Value,"lte" => v <= c.Value,"gt" => v > c.Value,"gte" => v >= c.Value,_ => false };
            if (!matched[i]) { starts.Remove(i); continue; }
            starts.TryAdd(i,sample.Elapsed);
            ready[i] = sample.Elapsed - starts[i] >= TimeSpan.FromSeconds(c.HoldSeconds);
        }
        var eligible = rule.Conditions.All is not null ? ready.All(x=>x) : ready.Any(x=>x);
        if (!eligible) return Result(matched.Any(x=>x) ? "Pending" : "Observing", discontinuity ? "采样间断，持续时间已重新计时。" : "阈值未满足、数据缺失或持续时间不足。");
        if (lastNotification is {} action && sample.Elapsed - action < TimeSpan.FromSeconds(rule.Cooldown.AfterActionSeconds)) return Result("Cooldown", "仍在通知冷却期。");
        while (notifications.TryPeek(out var time) && sample.Elapsed - time >= TimeSpan.FromHours(1)) notifications.Dequeue();
        if (notifications.Count >= rule.Cooldown.MaxActionsPerHour) return Result("HourlyLimit", "达到每小时通知次数限制。");
        var messages = rule.Actions.Where(a=>a.Type == "notify" && a.Enabled).Select(a=>$"[{a.Level}] {a.Message}").ToArray();
        if (messages.Length == 0) return Result("Excluded", "没有启用的通知；所有修剪候选均被 M3 执行策略排除，不枚举目标、不估算释放量。");
        lastNotification = sample.Elapsed;
        notifications.Enqueue(sample.Elapsed);
        return Result("Notified", "仅通知；修剪动作禁用，前台/系统/名单/年龄保护不可关闭。",messages);
    }
}
