// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Presentation;

namespace SysBox.App.Views;

public sealed partial class DashboardPage : Page, IPageLifetime
{
    public DashboardViewModel ViewModel { get; } = App.Services.GetRequiredService<DashboardViewModel>();

    public DashboardPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
        Unloaded += (_, _) => Deactivate();
    }

    public void Deactivate() => ViewModel.Deactivate();
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
