// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Presentation;
using SysBox.Abstractions;
using SysBox.Transport;

namespace SysBox.App.Views;

public sealed partial class DashboardPage : Page, IPageLifetime
{
    public DashboardViewModel ViewModel { get; } = App.Services.GetRequiredService<DashboardViewModel>();
    private CancellationTokenSource? sampling;

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += (_, _) => { ViewModel.PropertyChanged -= ViewModel_Changed; ViewModel.PropertyChanged += ViewModel_Changed; sampling?.Cancel(); sampling = new(); _ = RefreshLoopAsync(sampling.Token); };
        Unloaded += (_, _) => { Deactivate(); ViewModel.PropertyChanged -= ViewModel_Changed; };
    }

    public void Deactivate()
    {
        sampling?.Cancel();
        ViewModel.Deactivate();
    }
    private async Task RefreshLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await ViewModel.RefreshAsync();
                var client = App.Services.GetRequiredService<RpcClient>();
                try
                {
                    var roots = await client.CallAsync<IndexRoot[]>("index.status", new
                    {
                    }, token);
                    token.ThrowIfCancellationRequested();
                    IndexSummary.Text = roots.Length == 0 ? "尚未建立索引" : $"已索引 {roots.Length} 个范围";
                }
                catch (OperationCanceledException) { throw; }
                catch { IndexSummary.Text = "索引状态暂不可用"; }
                try
                {
                    var volumes = await client.CallAsync<VolumeCapacity[]>("volumes", new
                    {
                    }, token);
                    token.ThrowIfCancellationRequested();
                    var capacity = DashboardCapacity.FromVolumes(volumes);
                    StorageSummary.Text = capacity.ReadyVolumes == 0 ? "容量暂不可用" : $"{capacity.ReadyVolumes} 个卷 · 可用 {capacity.AvailableBytes / 1_000_000_000m:N2} / {capacity.TotalBytes / 1_000_000_000m:N2} GB";
                    StorageUsageBar.Visibility = capacity.UsedPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
                    StorageUsageBar.Value = capacity.UsedPercent ?? 0;
                    StorageUsageText.Text = capacity.UsedPercent is { } used ? $"磁盘已用 {used:F1}%" + (capacity.ReadyVolumes < capacity.ReportedVolumes ? $"（仅计入可读取的 {capacity.ReadyVolumes}/{capacity.ReportedVolumes} 个卷）" : "") : "磁盘用量暂不可用";
                }
                catch (OperationCanceledException) { throw; }
                catch { StorageSummary.Text = "容量暂不可用"; StorageUsageBar.Visibility = Visibility.Collapsed; StorageUsageText.Text = "磁盘用量暂不可用"; }
                await Task.Delay(5000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void ViewModel_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DashboardViewModel.Snapshot))
            return;
        var used = MemoryFormatting.PhysicalPercentage(ViewModel.Snapshot);
        MemoryUsageBar.Visibility = used.HasValue ? Visibility.Visible : Visibility.Collapsed;
        MemoryUsageBar.Value = used ?? 0;
        MemoryUsageText.Text = used is { } percent ? $"内存已用 {percent:F1}%" : "内存用量暂不可用";
    }
    private void Navigate_Click(object sender, RoutedEventArgs args) =>
        App.MainWindow.SelectPage((string)((HyperlinkButton)sender).Tag);
}
