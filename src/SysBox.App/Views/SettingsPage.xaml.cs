// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using SysBox.App.Services;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Transport;

namespace SysBox.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ThemeService theme = App.Services.GetRequiredService<ThemeService>();
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReadNtfsAsync();
        Unloaded += (_, _) => ntfsCancellation?.Cancel();
        ThemePicker.SelectedIndex = (int)theme.Current;
        Loaded += async (_, _) => { await App.DesktopReady; if (!IsLoaded) return; BallEnabled.IsChecked = App.Desktop.Preferences.FloatingBall; TrayEnabled.IsChecked = App.Desktop.Preferences.KeepInTray; HotKeyPicker.SelectedIndex = App.Desktop.Preferences.HotKey; DesktopStatus.Text = App.Desktop.Status; };
        Loaded += (_, _) => { try { StartupEnabled.IsChecked = SysBox.Native.StartupRegistration.IsEnabled(); } catch { StartupStatus.Text = "无法读取当前用户的登录启动设置。"; } };
        Loaded += (_, _) => { ActualThemeChanged += OnActualThemeChanged; UpdateThemeLabel(); };
        Unloaded += (_, _) => ActualThemeChanged -= OnActualThemeChanged;
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemePicker.SelectedIndex >= 0)
        {
            theme.Apply((AppTheme)ThemePicker.SelectedIndex);
            if (App.Desktop is not null)
                App.Desktop.SaveTheme(ThemePicker.SelectedIndex);
            UpdateThemeLabel();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateThemeLabel();
    private async void SaveDesktop_Click(object sender, RoutedEventArgs args)
    {
        await App.Desktop.ConfigureAsync(HotKeyPicker.SelectedIndex, TrayEnabled.IsChecked == true, BallEnabled.IsChecked == true);
        DesktopStatus.Text = App.Desktop.Status;
    }
    private void QuickSearch_Click(object sender, RoutedEventArgs args) => App.Desktop.ShowSearch();
    private void Exit_Click(object sender, RoutedEventArgs args) => App.Desktop.Exit();
    private void Startup_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            SysBox.Native.StartupRegistration.SetEnabled(StartupEnabled.IsChecked == true);
            StartupStatus.Text = "登录启动设置已保存。";
        }
        catch (Exception e) { StartupStatus.Text = e.Message; }
    }
    private void UpdateThemeLabel() => AppliedThemeText.Text = ActualTheme == ElementTheme.Dark
        ? "当前显示：深色" : "当前显示：浅色";
}
