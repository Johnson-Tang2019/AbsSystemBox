// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.App.Services;
using SysBox.App.Views;
using SysBox.Core;
using Windows.Graphics;

namespace SysBox.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1160, 800));
        RootGrid.Loaded += ResizeForDisplay;
        App.Services.GetRequiredService<ThemeService>().Attach(RootGrid);
        DemoBanner.IsOpen = App.Services.GetRequiredService<IDashboardClient>().IsDemo;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Closed += (_, _) =>
        {
            if (RootFrame.Content is IPageLifetime page) page.Deactivate();
            RootFrame.Content = null;
        };
    }

    private void ResizeForDisplay(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= ResizeForDisplay;
        var scale = RootGrid.XamlRoot.RasterizationScale;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Min(1160 * scale, area.Width * 0.9),
            (int)Math.Min(800 * scale, area.Height * 0.9)));
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string route) return;
        Navigate(route);
    }

    public void SelectPage(string route)
    {
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == route);
        if (item is not null) Navigation.SelectedItem = item;
    }

    private void Navigate(string route)
    {
        var pageType = route switch
        {
            "memory" => typeof(MemoryPage),
            "search" => typeof(SearchPage),
            "storage" => typeof(StoragePage),
            "rules" => typeof(RulesPage),
            "diagnostics" => typeof(DiagnosticsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage)
        };
        if (RootFrame.CurrentSourcePageType == pageType) return;
        if (RootFrame.Content is IPageLifetime page) page.Deactivate();
        RootFrame.Navigate(pageType);
        // There are seven fixed destinations: keep at most one view of each, never an unbounded back stack.
        if (RootFrame.Content is Page current) current.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        RootFrame.BackStack.Clear();
    }
}
