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
using SysBox.FileIndex;
using SysBox.Transport;

namespace SysBox.App;

public partial class App : Application
{
    private Window? _window;
    private Mutex? instanceMutex;
    public static MainWindow MainWindow { get; private set; } = null!;
    public static ServiceProvider Services { get; private set; } = null!;
    public static DesktopController Desktop { get; private set; } = null!;
    public static Task DesktopReady { get; private set; } = Task.CompletedTask;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        instanceMutex = new Mutex(false, "Local\\AbsSystemBox.Desktop");
#if DEBUG
        if (Guid.TryParseExact(Environment.GetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX"), "N", out var fixtureId))
        {
            instanceMutex.Dispose();
            instanceMutex = new Mutex(false, "Local\\AbsSystemBox.Desktop.Test." + fixtureId.ToString("N"));
        }
#endif
        bool acquired;
        try
        {
            acquired = instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            instanceMutex.Dispose();
            instanceMutex = null;
            Exit();
            return;
        }
        var services = new ServiceCollection();
        services.AddSingleton<WindowsCoolingSensorClient>();
        services.AddSingleton<ICoolingSensorSource>(sp => sp.GetRequiredService<WindowsCoolingSensorClient>());
        services.AddSingleton<RpcClient>();
        services.AddSingleton<IMemoryMonitor, RemoteMemory>();
        services.AddSingleton<IProcessMemoryMonitor, RemoteProcesses>();
        services.AddSingleton<IDashboardClient, DashboardClient>();
        services.AddSingleton<IBatteryMonitor, WindowsBatteryMonitor>();
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--demo", StringComparer.Ordinal))
        {
            var demoRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AbsSystemBox-Demo-" + Environment.ProcessId);
            Environment.SetEnvironmentVariable("SYSBOX_DEMO_MODE", "1");
            Environment.SetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY", demoRoot);
            Environment.SetEnvironmentVariable("SYSBOX_TEST_INDEX_DIRECTORY", System.IO.Path.Combine(demoRoot, "index"));
            Environment.SetEnvironmentVariable("SYSBOX_TEST_PIPE_SUFFIX", "demo" + Environment.ProcessId);
            services.AddSingleton<IDashboardClient, DemoDashboardClient>();
            services.AddSingleton<IMemoryMonitor, DemoMemoryMonitor>();
            services.AddSingleton<IProcessMemoryMonitor, DemoProcessMemoryMonitor>();
            services.AddSingleton<IBatteryMonitor, DemoBatteryMonitor>();
        }
#endif
        services.AddSingleton<ICoolingDisplayTransport, GoldenFiledDisplayTransport>();
        services.AddSingleton<CoolingDisplayViewModel>();
        services.AddSingleton<ThemeService>();
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<ISamplingDelay, SamplingDelay>();
        services.AddTransient<MemoryViewModel>();
        services.AddTransient<BatteryViewModel>();
        var ruleDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysBox");
#if DEBUG
        ruleDirectory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY") ?? ruleDirectory;
#endif
        services.AddSingleton(new CoolingDisplayPreferenceStore(ruleDirectory));
        services.AddSingleton<RemoteRules>();
        services.AddSingleton<IRuleService>(sp => sp.GetRequiredService<RemoteRules>());
        services.AddSingleton<IRuleReview>(sp => sp.GetRequiredService<RemoteRules>());
        services.AddSingleton<IRuleObservation, RemoteObservation>();
        services.AddTransient<RulesViewModel>();
        services.AddSingleton<IFileIndexService, RemoteIndex>();
        services.AddSingleton<IIndexedSearchService, RemoteSearch>();
        services.AddTransient<SearchViewModel>();
        services.AddSingleton<IStorageBrowser, RemoteStorage>();
        services.AddTransient(sp => new StorageViewModel(sp.GetRequiredService<IStorageBrowser>(), () => new IndexControl()));
        services.AddTransient(sp => new IndexViewModel(sp.GetRequiredService<IFileIndexService>(), () => new IndexControl()));
        Services = services.BuildServiceProvider();
        MainWindow = new MainWindow();
        _window = MainWindow;
        Desktop = new DesktopController(ruleDirectory);
        MainWindow.AppWindow.Closing += (_, eventArgs) =>
        {
            if (Desktop.Preferences.KeepInTray && Desktop.CanKeepInTray && !Desktop.Exiting)
            {
                eventArgs.Cancel = true;
                MainWindow.AppWindow.Hide();
            }
        };
        _window.Closed += (_, _) =>
        {
            Services.GetRequiredService<CoolingDisplayViewModel>().Deactivate();
            Desktop.Dispose();
            try
            {
                Services.GetRequiredService<RpcClient>().Call<bool>("host.stop", new
                {
                });
            }
            catch (SysBoxOperationException) { }
            Services.Dispose();
            instanceMutex?.ReleaseMutex();
            instanceMutex?.Dispose();
            instanceMutex = null;
        };
        _window.Activate();
        DesktopReady = Desktop.StartAsync();
        if (!Services.GetRequiredService<IDashboardClient>().IsDemo)
            _ = Services.GetRequiredService<CoolingDisplayViewModel>().StartAtApplicationLaunchAsync();
    }
}
