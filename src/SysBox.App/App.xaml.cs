// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SysBox.Abstractions;
using SysBox.App.Services;
using SysBox.Core;
using SysBox.Memory;
using SysBox.Presentation;

namespace SysBox.App;

public partial class App : Application
{
    private Window? _window;
    public static MainWindow MainWindow { get; private set; } = null!;
    public static ServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMemoryMonitor, UnavailableMemoryMonitor>();
        services.AddSingleton<IDashboardClient, DashboardClient>();
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.Ordinal))
            services.AddSingleton<IDashboardClient, DemoDashboardClient>();
#endif
        services.AddSingleton<ThemeService>();
        services.AddTransient<DashboardViewModel>();
        Services = services.BuildServiceProvider();
        MainWindow = new MainWindow();
        _window = MainWindow;
        _window.Closed += (_, _) => Services.Dispose();
        _window.Activate();
    }
}
