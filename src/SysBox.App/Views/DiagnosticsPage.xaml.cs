// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace SysBox.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsPage()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        EnvironmentText.Text = $"应用版本：{version}\n{RuntimeInformation.OSDescription}\n{RuntimeInformation.FrameworkDescription}\n进程架构：{RuntimeInformation.ProcessArchitecture}";
        Loaded += (_, _) => ScaleText.Text = $"窗口缩放：{XamlRoot.RasterizationScale:P0}";
    }
}
