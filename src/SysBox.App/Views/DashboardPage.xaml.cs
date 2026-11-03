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
        Loaded += (_, _) => { sampling?.Cancel(); sampling = new(); _ = RefreshLoopAsync(sampling.Token); };
        Unloaded += (_, _) => Deactivate();
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
                    var ready = volumes.Where(v => v.TotalBytes is > 0 && v.AvailableBytes is not null).ToArray();
                    StorageSummary.Text = ready.Length == 0 ? "容量暂不可用" : $"{ready.Length} 个卷 · 可用 {SysBox.Presentation.SizeFormatting.Format(ready.Sum(v => v.AvailableBytes!.Value))}";
                }
                catch (OperationCanceledException) { throw; }
                catch { StorageSummary.Text = "容量暂不可用"; }
                await Task.Delay(5000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void Cards_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 760;
        FrameworkElement[] cards = [MemoryCard, SearchCard, StorageCard];
        for (var index = 0; index < cards.Length; index++)
        {
            Grid.SetRow(cards[index], wide ? 0 : index);
            Grid.SetColumn(cards[index], wide ? index : 0);
            Grid.SetColumnSpan(cards[index], wide ? 1 : 3);
        }
    }
    private void Navigate_Click(object sender, RoutedEventArgs args) =>
        App.MainWindow.SelectPage((string)((HyperlinkButton)sender).Tag);
}
