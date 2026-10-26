// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using SysBox.App.Services;

namespace SysBox.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ThemeService theme = App.Services.GetRequiredService<ThemeService>();
    public SettingsPage()
    {
        InitializeComponent();
        ThemePicker.SelectedIndex = (int)theme.Current;
        Loaded += (_, _) => { ActualThemeChanged += OnActualThemeChanged; UpdateThemeLabel(); };
        Unloaded += (_, _) => ActualThemeChanged -= OnActualThemeChanged;
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ThemePicker.SelectedIndex >= 0)
        {
            theme.Apply((AppTheme)ThemePicker.SelectedIndex);
            UpdateThemeLabel();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateThemeLabel();
    private void UpdateThemeLabel() => AppliedThemeText.Text = ActualTheme == ElementTheme.Dark
        ? "当前显示：深色" : "当前显示：浅色";
}
