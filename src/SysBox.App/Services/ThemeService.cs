// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;

namespace SysBox.App.Services;

public enum AppTheme { System, Light, Dark }

public sealed class ThemeService
{
    private FrameworkElement? root;
    public AppTheme Current { get; private set; }

    public void Attach(FrameworkElement element)
    {
        root = element;
        Apply(Current);
    }

    public void Apply(AppTheme theme)
    {
        Current = theme;
        if (root is not null)
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
    }
}
