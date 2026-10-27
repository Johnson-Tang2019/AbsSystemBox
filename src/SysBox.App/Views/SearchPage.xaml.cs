// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Presentation;
using Windows.Storage.Pickers;
using SysBox.Abstractions;
using Windows.ApplicationModel.DataTransfer;

namespace SysBox.App.Views;

public sealed partial class SearchPage : Page, IPageLifetime
{
    private bool dialogOpen;
    public IndexViewModel ViewModel { get; } = App.Services.GetRequiredService<IndexViewModel>();
    public SearchViewModel Search { get; } = App.Services.GetRequiredService<SearchViewModel>();
    public SearchPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += async (_, _) => { Search.Activate(); await ViewModel.RefreshAsync(); };
        Unloaded += (_, _) => Deactivate();
    }
    public void Deactivate() { ViewModel.Deactivate(); Search.Deactivate(); }
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
            await ShowDialogAsync(dialog);
        }
    }
    private async void Scan_Click(object sender, RoutedEventArgs args) { await ViewModel.ScanAsync(); await Search.FirstPageAsync(); }
    private void Pause_Click(object sender, RoutedEventArgs args) => ViewModel.TogglePause();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    private async void Clear_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new ContentDialog { Title = "清除索引缓存？", Content = "将清除本用户的全部文件索引，源文件和内存规则不会改动。以后需要重新扫描。",
            PrimaryButtonText = "清除缓存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) { await ViewModel.ClearAsync(); await Search.FirstPageAsync(); }
    }
    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs args) => Search.Sort = (SearchSort)((ComboBox)sender).SelectedIndex;
    private async void Search_Click(object sender, RoutedEventArgs args) => await Search.FirstPageAsync();
    private async void Previous_Click(object sender, RoutedEventArgs args) => await Search.PreviousPageAsync();
    private async void Next_Click(object sender, RoutedEventArgs args) => await Search.NextPageAsync();
    private async void CopyPath_Click(object sender, RoutedEventArgs args) => await CopyAsync((string)((MenuFlyoutItem)sender).Tag);
    private async void Reveal_Click(object sender, RoutedEventArgs args) => await RevealAsync((string)((MenuFlyoutItem)sender).Tag);
    private async void Open_Click(object sender, RoutedEventArgs args) => await OpenAsync((string)((MenuFlyoutItem)sender).Tag);
    private async void Result_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs args)
    {
        if (Search.Selected is { } row) await OpenAsync(row.Path);
    }
    private void Result_SelectionChanged(object sender, SelectionChangedEventArgs args) => Search.Selected = ((ListView)sender).SelectedItem as SearchResultRow;
    private async void CopySelected_Click(object sender, RoutedEventArgs args)
    {
        if (Search.Selected is not { } row) return;
        await CopyAsync(row.Path);
    }
    private async Task CopyAsync(string path)
    {
        try { var data = new DataPackage(); data.SetText(path); Clipboard.SetContent(data); }
        catch { await ShowErrorAsync(); }
    }
    private async void RevealSelected_Click(object sender, RoutedEventArgs args) { if (Search.Selected is { } row) await RevealAsync(row.Path); }
    private async void OpenSelected_Click(object sender, RoutedEventArgs args) { if (Search.Selected is { } row) await OpenAsync(row.Path); }
    private async Task RevealAsync(string path)
    {
        try { SysBox.Native.ShellFileActions.Reveal(path); }
        catch { await ShowErrorAsync(); }
    }
    private async Task OpenAsync(string path)
    {
        var dialog = new ContentDialog { Title = "打开这个文件？", Content = path + "\nWindows 将使用关联程序打开；可执行文件或脚本可能运行代码。",
            PrimaryButtonText = "打开", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || !IsLoaded) return;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path) || (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))) throw new System.IO.FileNotFoundException();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { await ShowErrorAsync(); }
    }
    private Task ShowErrorAsync() => ShowErrorCoreAsync();
    private async Task ShowErrorCoreAsync()
    {
        var dialog = new ContentDialog { Title = "无法访问这个位置", Content = "文件可能已经移动、离线或没有权限，请重新扫描后重试。", CloseButtonText = "知道了", XamlRoot = XamlRoot };
        await ShowDialogAsync(dialog);
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen || !IsLoaded) return ContentDialogResult.None;
        dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        catch { return ContentDialogResult.None; }
        finally { dialogOpen = false; }
    }
}
