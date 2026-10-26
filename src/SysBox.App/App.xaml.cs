// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using SysBox.Abstractions;
using SysBox.App.Services;
using SysBox.Core;
using SysBox.Memory;
using SysBox.Native;
using SysBox.Presentation;
using SysBox.Rules;

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
        services.AddSingleton<IWindowsMemoryApi, WindowsMemoryApi>();
        services.AddSingleton<IMemoryMonitor, WindowsMemoryMonitor>();
        services.AddSingleton<IProcessMemoryMonitor, WindowsProcessMemoryMonitor>();
        services.AddSingleton<IDashboardClient, DashboardClient>();
        services.AddSingleton<IBatteryMonitor, WindowsBatteryMonitor>();
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.Ordinal))
        {
            services.AddSingleton<IDashboardClient, DemoDashboardClient>();
            services.AddSingleton<IMemoryMonitor, DemoMemoryMonitor>();
            services.AddSingleton<IProcessMemoryMonitor, DemoProcessMemoryMonitor>();
            services.AddSingleton<IBatteryMonitor, DemoBatteryMonitor>();
        }
#endif
        services.AddSingleton<ThemeService>();
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<ISamplingDelay, SamplingDelay>();
        services.AddTransient<MemoryViewModel>();
        services.AddTransient<BatteryViewModel>();
        var ruleDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysBox");
#if DEBUG
        ruleDirectory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY") ?? ruleDirectory;
#endif
        services.AddSingleton(new RuleWorkspace(ruleDirectory));
        services.AddSingleton<IRuleService>(sp => new RuleDraftService(sp.GetRequiredService<RuleWorkspace>()));
        services.AddSingleton<IRuleReview>(sp => sp.GetRequiredService<RuleWorkspace>());
        services.AddSingleton<RuleObserver>();
        services.AddTransient<RulesViewModel>();
        Services = services.BuildServiceProvider();
        MainWindow = new MainWindow();
        _window = MainWindow;
        _window.Closed += (_, _) => Services.Dispose();
        _window.Activate();
        Services.GetRequiredService<RuleObserver>().Start();
    }
}
