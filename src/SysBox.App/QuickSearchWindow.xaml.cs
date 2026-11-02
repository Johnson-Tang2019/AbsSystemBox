// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using SysBox.Presentation;
using Windows.System;
using Windows.Graphics;

namespace SysBox.App;

public sealed partial class QuickSearchWindow : Window
{
    public SearchViewModel ViewModel { get; } = App.Services.GetRequiredService<SearchViewModel>();
    public QuickSearchWindow()
    {
        InitializeComponent();
        Root.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        var area = DisplayArea.GetFromWindowId(App.MainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = ((FrameworkElement)App.MainWindow.Content).XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Min((int)(720 * scale), Math.Max(320, area.Width - 32));
        var height = Math.Min((int)(480 * scale), Math.Max(240, area.Height - 48));
        AppWindow.Resize(new(width, height));
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 3)));
        Root.Loaded += (_, _) => { ViewModel.Activate(); QueryBox.Focus(FocusState.Programmatic); };
        Closed += (_, _) => ViewModel.Deactivate();
    }
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs args) => ViewModel.Selected = Results.SelectedItem as SearchResultRow;
    private void Results_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is SearchResultRow row)
            Reveal(row);
    }
    private void Reveal_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.Selected is { } row)
            Reveal(row);
    }
    private void Reveal(SearchResultRow row)
    {
        try
        {
            SysBox.Native.ShellFileActions.Reveal(row.Path);
        }
        catch { QueryBox.PlaceholderText = "文件已移动或无法访问，请重扫对应目录。"; }
    }
    private void Main_Click(object sender, RoutedEventArgs args)
    {
        App.Desktop.ShowMain();
        App.MainWindow.SelectPage("search");
        Close();
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Close();
        }
        if (args.Key == VirtualKey.Down && ViewModel.Results.Count > 0)
        {
            Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, ViewModel.Results.Count - 1);
            args.Handled = true;
        }
        if (args.Key == VirtualKey.Up && ViewModel.Results.Count > 0)
        {
            Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
            args.Handled = true;
        }
        if (args.Key == VirtualKey.Enter && ViewModel.Selected is { } row)
        {
            args.Handled = true;
            Reveal(row);
        }
    }
}
