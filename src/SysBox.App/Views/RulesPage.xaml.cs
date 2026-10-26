// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Presentation;
using SysBox.Rules;

namespace SysBox.App.Views;

public sealed partial class RulesPage : Page, IPageLifetime
{
    private readonly RulesViewModel model;
    private readonly IRuleReview reviewer;
    private readonly RuleObserver observer;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public RulesPage()
    {
        InitializeComponent();
        model = App.Services.GetRequiredService<RulesViewModel>();
        reviewer = App.Services.GetRequiredService<IRuleReview>();
        observer = App.Services.GetRequiredService<RuleObserver>();
        DataContext = model;
        Loaded += (_,_) => { Load(); timer.Start(); UpdateObservation(); };
        Unloaded += (_,_) => Deactivate();
        timer.Tick += (_,_) => UpdateObservation();
    }
    public void Deactivate() => timer.Stop();
    private void UpdateObservation() { ObserverStatus.Text = observer.Status; NotificationText.Text = observer.Notification; }
    private void Load()
    {
        model.Load();
        VersionsBox.ItemsSource = model.State?.Versions.Select(v=>new VersionItem(v.Revision,$"v{v.Revision} · {v.Summary}")).Reverse().ToArray();
        VersionsBox.SelectedIndex = model.State?.Versions.Length > 1 ? 1 : 0;
    }
    private void Reload_Click(object sender,RoutedEventArgs e) => Load();
    private void Validate_Click(object sender,RoutedEventArgs e) => model.Validate();
    private void Simulate_Click(object sender,RoutedEventArgs e) => model.Simulate(observer.Samples);
    private void Propose_Click(object sender,RoutedEventArgs e) => model.Propose(ReasonBox.Text,observer.Samples);
    private void Rollback_Click(object sender,RoutedEventArgs e)
    {
        if (VersionsBox.SelectedItem is not VersionItem target || model.State is not {} state) return;
        try { model.SetPending(reviewer.ProposeRollback(target.Revision,state.Active.Revision,state.Active.Sha256,observer.Samples)); }
        catch(RuleException error) { model.Report($"{error.Code}: {error.Message}"); }
    }
    private async void Review_Click(object sender,RoutedEventArgs e)
    {
        if (model.Pending is not {} proposal) { model.Report("没有待审批提案。请先提出修改。"); return; }
        var content = new StackPanel { Spacing = 12, MaxWidth = 650 };
        content.Children.Add(new TextBlock { Text = $"原因：{proposal.Reason}\n基准：v{proposal.BaseRevision}\nSHA-256：{proposal.Sha256}\n通知模拟：{proposal.Simulation.NotificationCount}；清理执行：0\n到期：{proposal.ExpiresUtc.ToLocalTime():HH:mm:ss}", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBox { Header = "当前配置 → 提案配置（完整内容）", Text = (model.State?.Active.Yaml ?? "") + "\n\n----- 提案 -----\n" + proposal.Yaml, IsReadOnly = true, AcceptsReturn = true, Height = 240, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "审批规则修改 · 仅通知", Content = content,
            PrimaryButtonText = "批准并应用", SecondaryButtonText = "拒绝", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(dialog,"RuleApprovalDialog");
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) { model.Report("审批已取消；有效规则未改变。"); return; }
            if (result == ContentDialogResult.Primary) { var version = reviewer.Approve(proposal.Id,proposal.Sha256); Load(); model.Report($"已批准并应用 v{version.Revision}；仅通知，不执行清理。"); }
            else { reviewer.Reject(proposal.Id,proposal.Sha256); Load(); model.Report("已拒绝；有效规则未改变。"); }
        }
        catch(RuleException error) { model.Report($"{error.Code}: {error.Message}"); }
    }
    private sealed record VersionItem(long Revision,string Summary);
}
