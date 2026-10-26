// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using SysBox.Presentation;

namespace SysBox.App.Views;

public sealed partial class MemoryPage : Page, IPageLifetime
{
    public MemoryViewModel ViewModel { get; } = App.Services.GetRequiredService<MemoryViewModel>();
    public MemoryPage()
    {
        InitializeComponent();
        // One bounded view per window avoids rebuilding hundreds of row templates on every visit.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        DataContext = ViewModel;
        Loaded += (_, _) => ViewModel.Start();
        Unloaded += (_, _) => Deactivate();
    }
    public void Deactivate()
    {
        ViewModel.Deactivate();
    }
    private void Metrics_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 760;
        FrameworkElement[] cards = [UsageCard, AvailableCard, CommitCard];
        for (var index = 0; index < cards.Length; index++)
        {
            Grid.SetRow(cards[index], wide ? 0 : index);
            Grid.SetColumn(cards[index], wide ? index : 0);
            Grid.SetColumnSpan(cards[index], wide ? 1 : 3);
        }
    }
}
