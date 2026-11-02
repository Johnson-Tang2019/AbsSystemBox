// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Presentation;
using SysBox.Rules;
namespace SysBox.UnitTests;

public sealed class RemoteRuleDisplayFeaturesTests
{
    private sealed class Service(RuleWorkspace workspace) : IRuleService
    {
        public int Validations; public bool Offline;
        public RuleState Read() => workspace.Read();
        public RuleValidation Validate(string yaml)
        {
            Validations++;
            if (Offline)
                throw new RuleException("CORE_DISCONNECTED", "后台断开。");
            return workspace.Validate(yaml);
        }
        public RuleReport Simulate(string yaml, IReadOnlyList<RuleSample> samples) => throw new NotSupportedException();
        public RuleProposal Propose(string yaml, long revision, string hash, string reason, IReadOnlyList<RuleSample> samples) => throw new NotSupportedException();
    }
    [Fact]
    public void BindingSummaryReadsCachedMetadataAndCannotStartRequestsAfterHostDisconnection()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-rule-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new Service(new RuleWorkspace(root));
            var model = new RulesViewModel(service);
            model.Load();
            Assert.Equal(1, service.Validations);
            var summary = model.ActiveSummary;
            service.Offline = true;
            Assert.Equal(summary, model.ActiveSummary);
            Assert.Equal(summary, model.ActiveSummary);
            Assert.Equal(1, service.Validations);
            model.Load();
            Assert.Contains("CORE_DISCONNECTED", model.Output);
            Assert.Equal(summary, model.ActiveSummary);
        }
        finally { Directory.Delete(root, true); }
    }
}
