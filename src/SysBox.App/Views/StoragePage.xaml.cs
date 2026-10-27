// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Presentation;
using SysBox.Abstractions;
using Windows.Storage.Pickers;

namespace SysBox.App.Views;

public sealed partial class StoragePage : Page, IPageLifetime
{
    public StorageViewModel ViewModel { get; } = App.Services.GetRequiredService<StorageViewModel>();
    public StoragePage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ViewModel.Current)) SpaceMap.Update(ViewModel.Current); };
        Unloaded += (_, _) => Deactivate();
    }
    public void Deactivate() => ViewModel.Deactivate();
    private async void Choose_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.IsBusy) return;
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && IsLoaded) ViewModel.Path = folder.Path;
        }
        catch { ViewModel.ReportError("无法打开文件夹选择器，请在输入框填写目录路径。 "); }
    }
    private async void Analyze_Click(object sender, RoutedEventArgs args) => await ViewModel.AnalyzeAsync();
    private async void Read_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadAsync(ViewModel.Path, resetScope: true);
    private void Pause_Click(object sender, RoutedEventArgs args) => ViewModel.TogglePause();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    private async void Back_Click(object sender, RoutedEventArgs args) => await ViewModel.BackAsync();
    private async void Forward_Click(object sender, RoutedEventArgs args) => await ViewModel.ForwardAsync();
    private async void Up_Click(object sender, RoutedEventArgs args) => await ViewModel.UpAsync();
    private async void Previous_Click(object sender, RoutedEventArgs args) => await ViewModel.PreviousPageAsync();
    private async void Next_Click(object sender, RoutedEventArgs args) => await ViewModel.NextPageAsync();
    private void Sort_Changed(object sender, SelectionChangedEventArgs args) => ViewModel.Sort = (StorageSort)((ComboBox)sender).SelectedIndex;
    private async void Child_Click(object sender, ItemClickEventArgs args) { if (args.ClickedItem is StorageRow { Item.IsDirectory: true } row) await ViewModel.LoadAsync(row.Path); }
    private async void Map_FolderInvoked(object? sender, string path) => await ViewModel.LoadAsync(path);
    private void Body_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var wide = args.NewSize.Width >= 760;
        Grid.SetRow(ChildrenList, 0); Grid.SetColumn(ChildrenList, 0); Grid.SetColumnSpan(ChildrenList, wide ? 1 : 2); Grid.SetRowSpan(ChildrenList, wide ? 2 : 1);
        Grid.SetRow(SpaceMap, wide ? 0 : 1); Grid.SetColumn(SpaceMap, wide ? 1 : 0); Grid.SetColumnSpan(SpaceMap, wide ? 1 : 2); Grid.SetRowSpan(SpaceMap, wide ? 2 : 1);
    }
}
