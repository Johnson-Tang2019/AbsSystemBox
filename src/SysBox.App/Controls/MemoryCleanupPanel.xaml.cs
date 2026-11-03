// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Transport;
namespace SysBox.App.Controls;

public sealed partial class MemoryCleanupPanel : UserControl
{
    private bool cleanupLoaded;
    public MemoryCleanupPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReadCleanupAsync();
        InitializeSystemAreas();
    }
    private async Task ReadCleanupAsync()
    {
        try
        {
            var state = await App.Services.GetRequiredService<RpcClient>().CallAsync<CleanupState>("cleanup.status", new
            {
            });
            var p = state.Policy;
            CleanupEnabled.IsChecked = p.Enabled;
            ThresholdEnabled.IsChecked = p.Threshold;
            PeriodicEnabled.IsChecked = p.Periodic;
            CleanupThreshold.Value = p.ThresholdPercent;
            CleanupHold.Value = p.HoldSeconds;
            CleanupPeriod.Value = p.PeriodMinutes;
            CleanupCooldown.Value = p.CooldownSeconds;
            CleanupMinimum.Value = p.MinimumWorkingSetMb;
            CleanupNames.Text = string.Join(", ", p.AllowedProcesses ?? []);
            cleanupLoaded = true;
            CleanupStatus.Text = state.Status + "\n" + string.Join("\n", state.Recent.TakeLast(8).Select(a => $"{a.TimestampUtc.ToLocalTime():HH:mm:ss} {a.Name}（{a.ProcessId}）：{a.Reason} 工作集 {a.BeforeBytes?.ToString() ?? "未知"} → {a.AfterBytes?.ToString() ?? "未知"} 字节"));
        }
        catch (Exception e)
        {
            cleanupLoaded = false;
            CleanupStatus.Text = "无法读取后台清理状态；未改变设置。";
            _ = e;
#if DEBUG
            var diagnosticDirectory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
            if (diagnosticDirectory is not null)
            {
                System.IO.Directory.CreateDirectory(diagnosticDirectory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(diagnosticDirectory, "connection-error.txt"), e.ToString());
            }
#endif
        }
    }
    private async void ReadCleanup_Click(object sender, RoutedEventArgs args) => await ReadCleanupAsync();
    private async void SaveCleanup_Click(object sender, RoutedEventArgs args)
    {
        if (!cleanupLoaded)
        {
            CleanupStatus.Text = "请先读取后台清理状态，避免覆盖未知配置。";
            return;
        }
        SaveCleanup.IsEnabled = false;
        try
        {
            var policy = new CleanupPolicy(CleanupEnabled.IsChecked == true, ThresholdEnabled.IsChecked == true, PeriodicEnabled.IsChecked == true,
                checked((int)CleanupThreshold.Value), checked((int)CleanupHold.Value), checked((int)CleanupPeriod.Value), checked((int)CleanupCooldown.Value), checked((int)CleanupMinimum.Value), 2,
                CleanupNames.Text.Split([',', '，', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            CleanupSchedule.Validate(policy);
            if (policy.Enabled)
            {
                var dialog = new ContentDialog { Title = "启用自动工作集清理？", Content = $"允许的进程：{string.Join(", ", policy.AllowedProcesses!)}\n阈值：{policy.ThresholdPercent}% 持续 {policy.HoldSeconds} 秒；定期：{(policy.Periodic ? policy.PeriodMinutes + " 分钟" : "关闭")}。\n可能增加后续调页，不保证释放物理内存。后台、前台保护、冷却和审计限制始终生效。", PrimaryButtonText = "确认启用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "CleanupApproval");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            await App.Services.GetRequiredService<RpcClient>().CallAsync<CleanupState>("cleanup.configure", policy);
            await ReadCleanupAsync();
        }
        catch (Exception e) { CleanupStatus.Text = "设置未生效：" + e.Message; }
        finally { SaveCleanup.IsEnabled = true; }
    }
}
