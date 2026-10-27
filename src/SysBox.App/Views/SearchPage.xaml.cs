// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Presentation;
using Windows.Storage.Pickers;

namespace SysBox.App.Views;

public sealed partial class SearchPage : Page, IPageLifetime
{
    public IndexViewModel ViewModel { get; } = App.Services.GetRequiredService<IndexViewModel>();
    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
        Unloaded += (_, _) => Deactivate();
    }
    public void Deactivate() => ViewModel.Deactivate();
    private void Root_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!ViewModel.IsBusy && ((ComboBox)sender).SelectedItem is SysBox.Abstractions.IndexRoot root) ViewModel.Root = root.Root;
    }
    private async void Choose_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.IsBusy) return;
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) ViewModel.Root = folder.Path;
        }
        catch
        {
            var dialog = new ContentDialog { Title = "无法打开目录选择器", Content = "请在输入框填写文件夹路径后重试。", CloseButtonText = "知道了", XamlRoot = XamlRoot };
            await dialog.ShowAsync();
        }
    }
    private async void Scan_Click(object sender, RoutedEventArgs args) => await ViewModel.ScanAsync();
    private void Pause_Click(object sender, RoutedEventArgs args) => ViewModel.TogglePause();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    private async void Clear_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new ContentDialog { Title = "清除索引缓存？", Content = "将清除本用户的全部文件索引，源文件和内存规则不会改动。以后需要重新扫描。",
            PrimaryButtonText = "清除缓存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearAsync();
    }
}
