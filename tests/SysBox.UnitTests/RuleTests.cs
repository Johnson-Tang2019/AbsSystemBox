// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Presentation;
using SysBox.Rules;

namespace SysBox.UnitTests;

public sealed class RuleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"SysBox-rules-tests",Guid.NewGuid().ToString("N"));
    private readonly RuleValidator validator = new();
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private MemoryRule Rule => validator.Validate(RuleValidator.DefaultYaml).Rule!;
    private static string Yaml(MemoryRule rule) => JsonSerializer.Serialize(rule,Options);
    private static RuleSample Sample(int seconds,ulong available=5,ulong? commit=90) => new(TimeSpan.FromSeconds(seconds),new(DateTimeOffset.UnixEpoch.AddSeconds(seconds),100,available,commit,100,DataCompleteness.Complete));
    private MemoryRule Fast => Rule with { Sampling = new(2,30), Conditions = new([new("available_memory_percent","lt",12,4)],null), Cooldown = new(6,2) };

    [Fact] public void DefaultIsCompleteAndSafe()
    {
        var result = validator.Validate(RuleValidator.DefaultYaml);
        Assert.True(result.IsValid,string.Join(";",result.Issues));
        Assert.True(result.Rule!.Simulation.DefaultDryRun);
        Assert.False(result.Rule.Actions[1].Enabled);
        Assert.True(validator.Validate(Yaml(result.Rule)).IsValid);
    }
    [Theory]
    [InlineData("x: &x [*x]","YAML_UNSAFE")]
    [InlineData("!!System.Object {}","YAML_UNSAFE")]
    [InlineData("x: 1\nx: 2","YAML_DUPLICATE")]
    [InlineData("x: [","YAML_SYNTAX")]
    [InlineData("---\nx: 1\n---\nx: 2","YAML_SYNTAX")]
    [InlineData("x: [[[[[[[[[[[[[1]]]]]]]]]]]]]","YAML_DEPTH")]
    public void RejectsUnsafeYaml(string yaml,string code) => Assert.Contains(validator.Validate(yaml).Issues,i=>i.Code == code);
    [Fact] public void RejectsSizeAndNodeBombs()
    {
        Assert.Equal("INPUT_SIZE",validator.Validate(new string('粉',23000)).Issues[0].Code);
        Assert.Equal("YAML_LIMIT",validator.Validate("[" + string.Join(',',Enumerable.Repeat("0",5000)) + "]").Issues[0].Code);
    }
    [Theory]
    [InlineData("schema_version: 1","schema_version: 2")]
    [InlineData("enabled: true","enabled: 'true'")]
    [InlineData("interval_seconds: 5","interval_seconds: 1")]
    [InlineData("value: 12","value: 101")]
    [InlineData("hold_seconds: 45","hold_seconds: 1")]
    [InlineData("after_action_seconds: 1200","after_action_seconds: 0")]
    [InlineData("max_actions_per_hour: 1","max_actions_per_hour: 0")]
    [InlineData("exclude_foreground: true","exclude_foreground: false")]
    [InlineData("exclude_system_processes: true","exclude_system_processes: false")]
    [InlineData("min_process_age_seconds: 120","min_process_age_seconds: 0")]
    [InlineData("enabled: false","enabled: true")]
    [InlineData("max_targets: 2","max_targets: 3")]
    [InlineData("only_user_processes: true","only_user_processes: false")]
    [InlineData("require_interactive_approval: true","require_interactive_approval: false")]
    [InlineData("default_dry_run: true","default_dry_run: false")]
    [InlineData("audit_enabled: true","audit_enabled: false")]
    [InlineData("include_raw_paths_in_logs: false","include_raw_paths_in_logs: true")]
    [InlineData("type: notify","type: powershell")]
    [InlineData("Code.exe","C:/Code.exe")]
    [InlineData("level: warning","command: run")]
    public void RejectsEveryUnsafeField(string old,string replacement) => Assert.False(validator.Validate(RuleValidator.DefaultYaml.Replace(old,replacement,StringComparison.Ordinal)).IsValid);
    [Fact] public void LogicAndHistoryMustBeConsistent()
    {
        Assert.False(validator.Validate(Yaml(Fast with { Conditions = new([],[]) })).IsValid);
        Assert.False(validator.Validate(Yaml(Fast with { Conditions = new(null,null) })).IsValid);
        Assert.False(validator.Validate(Yaml(Fast with { Conditions = new([new("available_memory_percent","lt",12,1801)],null) })).IsValid);
    }
    [Fact] public void HoldCooldownAndHourlyLimitAreExact()
    {
        var r = Fast;
        var report = RuleSimulator.Replay(Yaml(r),r,Enumerable.Range(0,15).Select(i=>Sample(i*2)).ToArray());
        Assert.Equal([4,10],report.Decisions.Where(d=>d.State == "Notified").Select(d=>(int)(d.TimestampUtc-DateTimeOffset.UnixEpoch).TotalSeconds));
        Assert.Contains(report.Decisions,d=>d.State == "Cooldown");
        Assert.Equal("HourlyLimit",report.Decisions[^1].State);
        Assert.Equal(0,report.ActuallyExecuted);
    }
    [Theory] [InlineData("lt",12,false)] [InlineData("lte",12,true)] [InlineData("gt",12,false)] [InlineData("gte",12,true)]
    public void ThresholdBoundary(string op,ulong available,bool notify)
    {
        var r = Fast with { Conditions = new([new("available_memory_percent",op,12,2)],null) };
        Assert.Equal(notify,RuleSimulator.Replay(Yaml(r),r,[Sample(0,available),Sample(2,available)]).NotificationCount == 1);
    }
    [Fact] public void MissingCommitAndGapsNeverManufactureContinuousPressure()
    {
        var r=Fast with { Conditions = new([new("committed_memory_percent","gt",80,4)],null) };
        var report=RuleSimulator.Replay(Yaml(r),r,[Sample(0),Sample(2,commit:null),Sample(4),Sample(20),Sample(22)]);
        Assert.Equal(0,report.NotificationCount);
        Assert.Contains(report.Decisions,d=>d.Reason.Contains("间断",StringComparison.Ordinal));
    }
    [Fact] public void AnyAllAndMonotonicTime()
    {
        RuleCondition[] conditions=[new("committed_memory_percent","gt",80,2),new("available_memory_percent","lt",12,2)];
        var any=Fast with { Conditions=new(null,conditions) };
        var all=Fast with { Conditions=new(conditions,null) };
        Assert.Equal(1,RuleSimulator.Replay(Yaml(any),any,[Sample(0,50),Sample(2,50)]).NotificationCount);
        Assert.Equal(0,RuleSimulator.Replay(Yaml(all),all,[Sample(0,50),Sample(2,50)]).NotificationCount);
        var engine=new RuleEngine(any);
        engine.Observe(Sample(4));
        Assert.Equal("InvalidTime",engine.Observe(Sample(2)).State);
        var shifted=Sample(6) with { Snapshot=Sample(10000).Snapshot };
        Assert.Equal("Pending",engine.Observe(shifted).State);
        Assert.Equal("Notified",engine.Observe(Sample(8)).State);
    }
    [Fact] public void DisabledAndNoNotifyDoNotExecute()
    {
        var disabled=Fast with { Enabled=false };
        Assert.Equal("Disabled",new RuleEngine(disabled).Observe(Sample(0)).State);
        var noNotify=Fast with { Actions=[Rule.Actions[1]] };
        Assert.Equal("Excluded",RuleSimulator.Replay(Yaml(noNotify),noNotify,[Sample(0),Sample(2),Sample(4)]).Decisions[^1].State);
    }
    [Fact] public void InvalidDraftNeverChangesActiveAndProposalIsNotApproval()
    {
        var store=new RuleWorkspace(directory);
        var old=store.Read().Active;
        Assert.Throws<RuleException>(()=>store.Propose("bad",1,old.Sha256,"test",[]));
        var proposal=store.Propose(Yaml(Fast),1,old.Sha256,"test",[Sample(0),Sample(4)]);
        Assert.Equal(old,store.Read().Active);
        Assert.Equal(proposal.Sha256,RuleSimulator.Hash(proposal.Yaml));
        Assert.DoesNotContain(typeof(IRuleService).GetMethods(),m=>m.Name is "Approve" or "Reject" or "ProposeRollback");
    }
    [Fact] public void ApprovalRestartRejectAndRollbackAreVersioned()
    {
        var store=new RuleWorkspace(directory);
        var old=store.Read().Active;
        var proposal=store.Propose(Yaml(Fast),1,old.Sha256,"change",[]);
        Assert.Equal("HASH_MISMATCH",Assert.Throws<RuleException>(()=>store.Approve(proposal.Id,"bad")).Code);
        var v2=store.Approve(proposal.Id,proposal.Sha256);
        Assert.Equal(2,v2.Revision);
        store=new(directory);
        Assert.Equal(v2,store.Read().Active);
        var rollback=store.ProposeRollback(1,2,v2.Sha256,[]);
        Assert.Equal(2,store.Read().Active.Revision);
        var v3=store.Approve(rollback.Id,rollback.Sha256);
        Assert.Equal(3,v3.Revision);
        Assert.Equal(Rule.Conditions.All,store.Validate(v3.Yaml).Rule!.Conditions.All);
        var rejected=store.Propose(v3.Yaml,3,v3.Sha256,"reject",[]);
        store.Reject(rejected.Id,rejected.Sha256);
        Assert.Equal("PROPOSAL_REVIEWED",Assert.Throws<RuleException>(()=>store.Approve(rejected.Id,rejected.Sha256)).Code);
        Assert.Equal(3,store.Read().Active.Revision);
        Assert.Equal(3,store.Read().Audit.Length);
        Assert.True(File.Exists(Path.Combine(directory,"rule-state.json.bak")));
    }
    [Fact] public void ConcurrentAndStaleProposalsCannotOverwrite()
    {
        var a=new RuleWorkspace(directory);var b=new RuleWorkspace(directory);
        var active=a.Read().Active;
        var p=a.Propose(active.Yaml,1,active.Sha256,"a",[]);
        var q=b.Propose(active.Yaml,1,active.Sha256,"b",[]);
        a.Approve(p.Id,p.Sha256);
        Assert.Equal("CONFLICT_REVISION",Assert.Throws<RuleException>(()=>b.Approve(q.Id,q.Sha256)).Code);
        Assert.Equal("CONFLICT_REVISION",Assert.Throws<RuleException>(()=>b.Propose(active.Yaml,1,active.Sha256,"stale",[])).Code);
        Assert.Equal(2,b.Read().Active.Revision);
    }
    [Fact] public void RevisionRewritePreservesYamlCommentsAndScalarNamedRevision()
    {
        var store=new RuleWorkspace(directory);var active=store.Read().Active;
        var yaml=RuleValidator.DefaultYaml.Replace("name: 平衡与安全","name: revision # keep comment",StringComparison.Ordinal);
        var p=store.Propose(yaml,1,active.Sha256,"format",[]);
        var version=store.Approve(p.Id,p.Sha256);
        Assert.Contains("name: revision # keep comment",version.Yaml);
        Assert.Contains("revision: 2",version.Yaml);
        Assert.True(store.Validate(version.Yaml).IsValid);
    }
    [Fact] public void ExpiredProposalAndCorruptStoreFailClosed()
    {
        var clock=new TestClock();var store=new RuleWorkspace(directory,clock);
        var active=store.Read().Active;
        var p=store.Propose(active.Yaml,1,active.Sha256,"expire",[]);
        clock.Now=clock.Now.AddMinutes(16);
        Assert.Equal("PROPOSAL_EXPIRED",Assert.Throws<RuleException>(()=>store.Approve(p.Id,p.Sha256)).Code);
        var path=Path.Combine(directory,"rule-state.json");
        File.WriteAllText(path,"{bad");
        Assert.Equal("STORE_CORRUPT",Assert.Throws<RuleException>(()=>store.Read()).Code);
        Assert.Equal("{bad",File.ReadAllText(path));
    }
    [Fact] public void StoreLockDoesNotLoseUpdates()
    {
        var store=new RuleWorkspace(directory);store.Read();
        using(var held=new FileStream(Path.Combine(directory,"rule-state.json.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.Equal("STORE_IO",Assert.Throws<RuleException>(()=>store.Read()).Code);
        Assert.Equal(1,store.Read().Active.Revision);
    }
    [Fact] public void StoreDirectoryFailureIsStructuredAndPreservesExistingFile()
    {
        Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"existing-file");File.WriteAllText(file,"keep");
        Assert.Equal("STORE_IO",Assert.Throws<RuleException>(()=>new RuleWorkspace(file).Read()).Code);
        Assert.Equal("keep",File.ReadAllText(file));
    }
    [Fact] public void NotificationLimitsSurviveRestartAndClockRollback()
    {
        var clock=new TestClock();var store=new RuleWorkspace(directory,clock);var initial=store.Read().Active;
        var proposal=store.Propose(Yaml(Fast),1,initial.Sha256,"rate",[]);var active=store.Approve(proposal.Id,proposal.Sha256);
        var decision=new RuleDecision(clock.Now,"Notified","test",["test"]);
        Assert.True(store.RecordNotification(2,active.Sha256,decision));
        store=new(directory,clock);
        clock.Now=clock.Now.AddSeconds(-2);Assert.False(store.RecordNotification(2,active.Sha256,decision));
        clock.Now=clock.Now.AddSeconds(8);Assert.True(store.RecordNotification(2,active.Sha256,decision));
        clock.Now=clock.Now.AddSeconds(6);Assert.False(store.RecordNotification(2,active.Sha256,decision));
        clock.Now=clock.Now.AddHours(1);Assert.True(store.RecordNotification(2,active.Sha256,decision));
        Assert.False((object)new RuleDraftService(store) is IRuleReview);
    }
    [Fact] public void ViewModelShowsErrorsAndDoesNotApplyOnPropose()
    {
        var store=new RuleWorkspace(directory);var model=new RulesViewModel(store);model.Load();
        model.Yaml="x: [";model.Validate();Assert.Contains("YAML_SYNTAX",model.Output);
        model.Propose("test",[]);Assert.Null(model.Pending);
        model.Load();model.Simulate([]);Assert.Contains("实际执行 0",model.Output);
        model.Propose("test",[]);Assert.NotNull(model.Pending);Assert.Equal(1,store.Read().Active.Revision);
    }
    public void Dispose() { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
