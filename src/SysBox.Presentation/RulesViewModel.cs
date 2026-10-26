// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;

namespace SysBox.Presentation;

public sealed class RulesViewModel(IRuleService rules) : ObservableObject
{
    private string yaml = "";
    private string output = "验证后可模拟并提出修改；提案不会立即生效。";
    public RuleState? State { get; private set; }
    public RuleProposal? Pending { get; private set; }
    public string Yaml { get => yaml; set => SetProperty(ref yaml,value); }
    public string Output { get=>output; private set=>SetProperty(ref output,value); }
    public string ActiveSummary => State is {} s ? $"{rules.Validate(s.Active.Yaml).Rule?.Name} · v{s.Active.Revision} · 仅通知\nSHA-256: {s.Active.Sha256}" : "规则存储不可用";
    public void Load(bool replaceEditor = true)
    {
        Guard(()=>
        {
            State = rules.Read();
            if (replaceEditor) Yaml = State.Active.Yaml;
            Pending = State.Proposals.LastOrDefault(p=>p.Status == "Pending" && p.ExpiresUtc > DateTimeOffset.UtcNow);
            OnPropertyChanged(nameof(ActiveSummary));
        });
    }
    public void Validate() => Guard(()=>
    {
        var result = rules.Validate(Yaml);
        Output = result.IsValid ? "校验通过 · 低风险通知；修剪动作禁用。" : string.Join("\n",result.Issues.Select(i=>$"{i.Code} {i.Path}: {i.Message}"));
    });
    public void Simulate(IReadOnlyList<RuleSample> samples) => Guard(()=> Output = Format(rules.Simulate(Yaml,samples)));
    public void Propose(string reason,IReadOnlyList<RuleSample> samples) => Guard(()=>
    {
        var state = State ?? throw new RuleException("NOT_LOADED","请重新加载规则。");
        Pending = rules.Propose(Yaml,state.Active.Revision,state.Active.Sha256,reason,samples);
        Output = $"提案 {Pending.Id} 已保存，15 分钟内待审批；有效规则未改变。\nSHA-256: {Pending.Sha256}\n" + Format(Pending.Simulation);
    });
    public void SetPending(RuleProposal proposal) { Pending = proposal; Output = "回滚提案已保存，仍需审批。\n" + Format(proposal.Simulation); }
    public void Report(string text) => Output = text;
    private void Guard(Action action)
    {
        try { action(); }
        catch(RuleException e) { Output = $"{e.Code}: {e.Message}"; }
    }
    private static string Format(RuleReport report) => $"模拟（dry-run）· 样本 {report.Decisions.Count} · 通知 {report.NotificationCount} · 实际执行 {report.ActuallyExecuted}\n释放量：未知，不估算。修剪目标：全部排除（M3 禁用）。\n" +
        string.Join("\n",report.Decisions.TakeLast(80).Select(d=>$"{d.TimestampUtc.ToLocalTime():HH:mm:ss} {d.State}: {d.Reason}"));
}
