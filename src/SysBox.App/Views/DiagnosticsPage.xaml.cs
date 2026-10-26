// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Abstractions;

namespace SysBox.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString();
        EnvironmentText.Text = $"应用版本：{version}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.FrameworkDescription}\n进程架构：{RuntimeInformation.ProcessArchitecture}";
        Loaded += (_, _) => ScaleText.Text = $"窗口缩放：{XamlRoot.RasterizationScale:P0}";
        Loaded += async (_,_) =>
        {
            var reading = await App.Services.GetRequiredService<IBatteryMonitor>().ReadAsync(CancellationToken.None);
            BatteryText.Text = $"电池报告：{reading.State} · {reading.Error ?? "Windows BatteryReport / C#/WinRT"}";
        };
    }
}
