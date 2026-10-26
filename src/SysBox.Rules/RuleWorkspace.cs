// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SysBox.Rules;

// Only the trusted desktop composition root receives this surface. It is not an Agent service.
public interface IRuleReview
{
    RuleVersion Approve(string proposalId, string exactHash);
    void Reject(string proposalId, string exactHash);
    RuleProposal ProposeRollback(long targetRevision, long expectedRevision, string expectedHash, IReadOnlyList<RuleSample> samples);
}

public sealed class RuleDraftService(RuleWorkspace workspace) : IRuleService
{
    public RuleValidation Validate(string yaml) => workspace.Validate(yaml);
    public RuleReport Simulate(string yaml,IReadOnlyList<RuleSample> samples) => workspace.Simulate(yaml,samples);
    public RuleState Read() => workspace.Read();
    public RuleProposal Propose(string yaml,long expectedRevision,string expectedHash,string reason,IReadOnlyList<RuleSample> samples) => workspace.Propose(yaml,expectedRevision,expectedHash,reason,samples);
}

public sealed class RuleWorkspace(string directory, TimeProvider? timeProvider = null) : IRuleService, IRuleReview
{
    private readonly RuleValidator validator = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly string statePath = Path.Combine(Path.GetFullPath(directory), "rule-state.json");
    public RuleValidation Validate(string yaml) => validator.Validate(yaml);
    public RuleReport Simulate(string yaml, IReadOnlyList<RuleSample> samples)
    {
        var validation = Validate(yaml);
        if (!validation.IsValid) throw new RuleException("INVALID_RULE", string.Join("; ",validation.Issues.Select(i=>$"{i.Code} {i.Path}: {i.Message}")));
        return RuleSimulator.Replay(yaml,validation.Rule!,samples);
    }
    public RuleState Read() => Transaction(state => (state,state), false);
    public RuleProposal Propose(string yaml, long expectedRevision, string expectedHash, string reason, IReadOnlyList<RuleSample> samples)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512) throw new RuleException("INVALID_REASON", "修改原因须为 1–512 字。");
        var report = Simulate(yaml,samples);
        if (report.Revision != expectedRevision) throw new RuleException("CONFLICT_REVISION", "草案 revision 必须与基准版本相同。");
        return Transaction(state =>
        {
            CheckRevision(state,expectedRevision,expectedHash);
            if (state.Proposals.Count(p=>p.Status == "Pending" && p.ExpiresUtc > clock.GetUtcNow()) >= 20) throw new RuleException("PROPOSAL_LIMIT", "待审提案超过 20 项。");
            var proposal = new RuleProposal(Guid.NewGuid().ToString("N"),expectedRevision,expectedHash,yaml,RuleSimulator.Hash(yaml),reason,clock.GetUtcNow().AddMinutes(15),report);
            return (state with { Proposals = state.Proposals.Append(proposal).TakeLast(100).ToArray() },proposal);
        });
    }
    public RuleProposal ProposeRollback(long targetRevision, long expectedRevision, string expectedHash, IReadOnlyList<RuleSample> samples)
    {
        var state = Read();
        CheckRevision(state,expectedRevision,expectedHash);
        var target = state.Versions.SingleOrDefault(v=>v.Revision == targetRevision) ?? throw new RuleException("VERSION_NOT_FOUND", "找不到保留的版本。");
        if (!Validate(target.Yaml).IsValid) throw new RuleException("INVALID_RULE", "历史版本校验失败。");
        var yaml = WithRevision(target.Yaml,expectedRevision);
        return Propose(yaml,expectedRevision,expectedHash,$"回滚到版本 {targetRevision} 的安全规则",samples);
    }
    public RuleVersion Approve(string proposalId, string exactHash) => Transaction(state =>
    {
        var proposal = Find(state,proposalId,exactHash);
        CheckRevision(state,proposal.BaseRevision,proposal.BaseSha256);
        var rule = Validate(proposal.Yaml).Rule ?? throw new RuleException("INVALID_RULE", "提案重新校验失败。");
        var yaml = WithRevision(proposal.Yaml,checked(state.Active.Revision + 1));
        if (!Validate(yaml).IsValid) throw new RuleException("INVALID_RULE", "新版本校验失败。");
        var version = new RuleVersion(rule.Revision+1,yaml,RuleSimulator.Hash(yaml),clock.GetUtcNow(),proposal.Reason);
        return (state with { Active = version, Versions = state.Versions.Append(version).TakeLast(100).ToArray(),
            Proposals = state.Proposals.Select(p=>p.Id == proposal.Id ? p with { Status = "Approved" } : p).ToArray(),
            Audit = state.Audit.Append(new(clock.GetUtcNow(),proposal.Id,"Approved",version.Revision,proposal.Reason)).TakeLast(500).ToArray() },version);
    });
    public void Reject(string proposalId, string exactHash) => Transaction(state =>
    {
        var proposal = Find(state,proposalId,exactHash);
        return (state with { Proposals = state.Proposals.Select(p=>p.Id == proposal.Id ? p with { Status = "Rejected" } : p).ToArray(),
            Audit = state.Audit.Append(new(clock.GetUtcNow(),proposal.Id,"Rejected",state.Active.Revision,proposal.Reason)).TakeLast(500).ToArray() },true);
    });
    internal bool RecordNotification(long revision,string hash,RuleDecision decision) => Transaction(state =>
    {
        CheckRevision(state,revision,hash);
        var cooldown = Validate(state.Active.Yaml).Rule!.Cooldown;
        var notified = state.Audit.Where(a=>a.Outcome == "Notified").ToArray();
        var now = clock.GetUtcNow();
        if(notified.Any(a=>now-a.TimestampUtc < TimeSpan.FromSeconds(cooldown.AfterActionSeconds)) ||
            notified.Count(a=>now-a.TimestampUtc < TimeSpan.FromHours(1)) >= cooldown.MaxActionsPerHour) return (state,false);
        return (state with { Audit = state.Audit.Append(new(clock.GetUtcNow(),"live-notification","Notified",revision,$"{hash}: {decision.Notifications.Length} 条应用内通知；清理执行 0")).TakeLast(500).ToArray() },true);
    });
    private RuleProposal Find(RuleState state,string id,string hash)
    {
        var p = state.Proposals.SingleOrDefault(p=>p.Id == id) ?? throw new RuleException("PROPOSAL_NOT_FOUND", "提案不存在。");
        if (p.Status != "Pending") throw new RuleException("PROPOSAL_REVIEWED", "提案已处理。");
        if (p.ExpiresUtc <= clock.GetUtcNow()) throw new RuleException("PROPOSAL_EXPIRED", "审批已超时，请重新提案。");
        if (p.Sha256 != hash || RuleSimulator.Hash(p.Yaml) != hash) throw new RuleException("HASH_MISMATCH", "提案哈希不一致。");
        return p;
    }
    private static void CheckRevision(RuleState state,long revision,string hash)
    {
        if (state.Active.Revision != revision || state.Active.Sha256 != hash) throw new RuleException("CONFLICT_REVISION", "有效版本已变化，请重新读取并合并。");
    }
    private static string WithRevision(string yaml,long revision)
    {
        var parser = new Parser(new StringReader(yaml));
        var depth = 0;
        var revisionValue = false;
        var key = true;
        while(parser.MoveNext())
        {
            if(parser.Current is MappingStart or SequenceStart) { if(depth == 1) key = true; depth++; }
            if(parser.Current is MappingEnd or SequenceEnd) depth--;
            if(parser.Current is not Scalar scalar || depth != 1) continue;
            if(!key && revisionValue) return yaml[..checked((int)scalar.Start.Index)] + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + yaml[checked((int)scalar.End.Index)..];
            revisionValue = key && scalar.Value == "revision";
            key = !key;
        }
        throw new RuleException("INVALID_RULE","缺少根 revision 字段。");
    }
    private T Transaction<T>(Func<RuleState,(RuleState State,T Result)> action,bool write = true)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            using var gate = new FileStream(statePath + ".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            RuleState state;
            if (File.Exists(statePath))
            {
                if (new FileInfo(statePath).Length > 32 * 1024 * 1024) throw new RuleException("STORE_CORRUPT", "规则存储超过限制。");
                state = JsonSerializer.Deserialize<RuleState>(File.ReadAllText(statePath)) ?? throw new RuleException("STORE_CORRUPT", "规则存储为空。");
                if (state.Active is null || state.Versions is null || state.Proposals is null || state.Audit is null || state.Versions.Length > 100 || state.Proposals.Length > 100 || state.Audit.Length > 500 ||
                    !Validate(state.Active.Yaml).IsValid || state.Active.Sha256 != RuleSimulator.Hash(state.Active.Yaml) || Validate(state.Active.Yaml).Rule!.Revision != state.Active.Revision ||
                    !state.Versions.Any(v=>v is not null && v.Revision == state.Active.Revision && v.Sha256 == state.Active.Sha256) ||
                    state.Versions.Any(v=>v is null || v.Yaml is null || v.Sha256 != RuleSimulator.Hash(v.Yaml)) ||
                    state.Proposals.Any(p=>p is null || p.Yaml is null || p.Simulation is null || p.Sha256 != RuleSimulator.Hash(p.Yaml)) || state.Audit.Any(a=>a is null) ||
                    state.Versions.Select(v=>v.Revision).Distinct().Count() != state.Versions.Length || state.Proposals.Select(p=>p.Id).Distinct().Count() != state.Proposals.Length)
                    throw new RuleException("STORE_CORRUPT", "规则存储校验失败；保留原文件，停止规则观察。");
            }
            else
            {
                var yaml = RuleValidator.DefaultYaml;
                var version = new RuleVersion(1,yaml,RuleSimulator.Hash(yaml),clock.GetUtcNow(),"默认仅通知");
                state = new(version,[version],[],[]);
            }
            var (next,result) = action(state);
            if (write && !ReferenceEquals(next,state) || !File.Exists(statePath))
            {
                var temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    { JsonSerializer.Serialize(file,next); if(file.Length > 32 * 1024 * 1024) throw new RuleException("STORE_LIMIT","规则存储达到 32 MiB 限制；未改变有效版本。"); file.Flush(true); }
                    if (File.Exists(statePath)) File.Replace(temp,statePath,statePath + ".bak");
                    else File.Move(temp,statePath);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            return result;
        }
        catch (JsonException) { throw new RuleException("STORE_CORRUPT", "规则存储损坏；未重置或覆盖。"); }
        catch (IOException) { throw new RuleException("STORE_IO", "存储被占用或读写失败，请重试。"); }
        catch (UnauthorizedAccessException) { throw new RuleException("STORE_ACCESS", "无权访问规则存储。"); }
    }
}
