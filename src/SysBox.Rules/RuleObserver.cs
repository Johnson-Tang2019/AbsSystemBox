// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Abstractions;

namespace SysBox.Rules;

public sealed class RuleObserver(RuleWorkspace rules, IMemoryMonitor memory) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<RuleSample> history = [];
    private readonly object gate = new();
    private readonly long origin = Stopwatch.GetTimestamp();
    private RuleEngine? engine;
    private string? hash;
    public string Status { get; private set; } = "等待真实内存采样。";
    public string Notification { get; private set; } = "默认只通知；所有内存清理动作均禁用。";
    public Task Completion { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<RuleSample> Samples { get { lock(gate) return history.ToArray(); } }
    public void Start() { if (Completion == Task.CompletedTask) Completion = RunAsync(cancellation.Token); }
    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var interval = 5;
            try
            {
                var state = await Task.Run(rules.Read,token);
                var rule = rules.Validate(state.Active.Yaml).Rule!;
                interval = rule.Sampling.IntervalSeconds;
                if (hash != state.Active.Sha256) { hash = state.Active.Sha256; engine = new(rule); }
                var result = await memory.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                if (result.IsSuccess)
                {
                    var sample = new RuleSample(Stopwatch.GetElapsedTime(origin),result.Value!);
                    lock(gate)
                    {
                        history.Add(sample);
                        history.RemoveAll(s=>sample.Elapsed-s.Elapsed > TimeSpan.FromMinutes(rule.Sampling.KeepHistoryMinutes));
                        if(history.Count > 1024) history.RemoveRange(0,history.Count-1024);
                    }
                    var decision = engine!.Observe(sample);
                    Status = $"v{state.Active.Revision} · {decision.State} · {decision.Reason}";
                    if (decision.Notifications.Length > 0)
                    {
                        if(await Task.Run(()=>rules.RecordNotification(state.Active.Revision,state.Active.Sha256,decision),token))
                            Notification = $"{decision.TimestampUtc.ToLocalTime():HH:mm:ss} · " + string.Join("；",decision.Notifications);
                        else Status = $"v{state.Active.Revision} · Cooldown · 持久化的全局通知冷却或每小时限制；未再次通知。";
                    }
                }
                else Status = "采样失败；未产生通知，持续时间将在采样间断后重计。";
            }
            catch (RuleException e) { Status = $"{e.Code}: {e.Message}"; engine = null; hash = null; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            try { await Task.Delay(TimeSpan.FromSeconds(interval),token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
