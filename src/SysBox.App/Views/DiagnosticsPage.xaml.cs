// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Abstractions;
using SysBox.Transport;
using System.Text.Json;
using Microsoft.UI.Xaml;

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
        Loaded += async (_, _) => await ReadHostAsync();
        Loaded += async (_, _) =>
        {
            var reading = await App.Services.GetRequiredService<IBatteryMonitor>().ReadAsync(CancellationToken.None);
            BatteryText.Text = SysBox.Core.BatteryHealth.Describe(reading);
        };
    }
    private async Task<JsonElement?> ReadHostAsync()
    {
        try
        {
            var report = await App.Services.GetRequiredService<RpcClient>().CallAsync<JsonElement>("diagnostics", new
            {
            });
            HostStatus.Text = $"后台在线 · API {report.GetProperty("apiVersion").GetString()}\n索引范围：{report.GetProperty("indexedScopes").GetInt32()} · 活动任务：{report.GetProperty("activeJobs").GetInt32()}\n后台私有提交：{report.GetProperty("privateBytes").GetInt64() / 1048576.0:F1} MiB · 句柄：{report.GetProperty("handles").GetInt32()}";
            return report;
        }
        catch { HostStatus.Text = "后台不可用或索引存储读取失败。重新打开应用后可重试；原索引与规则保留。"; return null; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await ReadHostAsync();
    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        ExportReport.IsEnabled = false;
        try
        {
            var report = await ReadHostAsync();
            if (report is null)
            {
                ExportStatus.Text = "读取失败，未生成报告。";
                return;
            }
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "AbsSystemBox-diagnostics" };
            picker.FileTypeChoices.Add("JSON 报告", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                ExportStatus.Text = "已取消导出。";
                return;
            }
            await Windows.Storage.FileIO.WriteTextAsync(file, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            ExportStatus.Text = "脱敏诊断报告已保存。";
        }
        catch { ExportStatus.Text = "无法保存报告，请检查目标文件的权限和可用空间。"; }
        finally { ExportReport.IsEnabled = true; }
    }
}
