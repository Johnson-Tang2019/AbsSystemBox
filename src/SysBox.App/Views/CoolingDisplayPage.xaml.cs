// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Presentation;
using SysBox.App.Services;
using SysBox.Core;
namespace SysBox.App.Views;

public sealed partial class CoolingDisplayPage : Page, IPageLifetime
{
    public CoolingDisplayViewModel ViewModel { get; } = App.Services.GetRequiredService<CoolingDisplayViewModel>();
    private readonly bool demo = App.Services.GetRequiredService<IDashboardClient>().IsDemo;
    public CoolingDisplayPage()
    {
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.Load();
        AutoStartMode.IsChecked = ViewModel.Settings.AutoStart;
        ManualMode.IsChecked = ViewModel.Settings.ManualValues;
        TemperatureValue.Value = ViewModel.Settings.ManualTemperature;
        FanValue.Value = ViewModel.Settings.ManualFanRpm;
        if (demo) AuthorizationStatus.Text = "示例预览不连接真实水冷屏。请使用普通模式检测设备与传感器。";
        ViewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModel.IsRunning)) { PopulatePickers(); UpdateControls(); } };
        Loaded += (_, _) => { PopulatePickers(); UpdateControls(); };
        Unloaded += (_, _) => Deactivate();
    }
    private CancellationTokenSource? authorization;
    private bool detecting;
    private int pageGeneration;
    public void Deactivate()
    {
        pageGeneration++;
        authorization?.Cancel();
        ViewModel.CancelPageDetection();
    }
    private async void Authorize_Click(object sender, RoutedEventArgs args)
    {
        if (demo || authorization is not null || ViewModel.IsRunning) return;
        var request = pageGeneration;
        authorization = new();
        AuthorizeButton.IsEnabled = DetectButton.IsEnabled = StartButton.IsEnabled = false;
        try
        {
            AuthorizationStatus.Text = "等待管理员授权与本次硬件监测确认…";
            await App.Services.GetRequiredService<WindowsCoolingSensorClient>().AuthorizeAsync(authorization.Token);
            if (request != pageGeneration) return;
            AuthorizationStatus.Text = "授权采集组件已连接，请检测并选择本次可用的传感器。";
            await DetectAsync();
        }
        catch (OperationCanceledException) { AuthorizationStatus.Text = "硬件采集授权已取消。"; }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { AuthorizationStatus.Text = "管理员授权已取消。"; }
        catch (Exception e) { AuthorizationStatus.Text = "授权采集未启用：" + e.Message; }
        finally { authorization.Dispose(); authorization = null; UpdateControls(); }
    }
    private void UpdateControls()
    {
        var editable = !demo && !ViewModel.IsRunning && !detecting && authorization is null;
        foreach (var control in OptionsPanel.Children.OfType<Control>()) control.IsEnabled = editable;
        DetectButton.IsEnabled = StartButton.IsEnabled = SaveButton.IsEnabled = editable;
        StopButton.IsEnabled = ViewModel.IsRunning;
        AuthorizeButton.IsEnabled = editable;
    }
    private async void Detect_Click(object sender, RoutedEventArgs args) => await DetectAsync();
    private async Task DetectAsync()
    {
        if (demo || detecting) return;
        detecting = true;
        UpdateControls();
        var request = pageGeneration;
        await ViewModel.RefreshAsync();
        detecting = false;
        if (request != pageGeneration) { UpdateControls(); return; }
        PopulatePickers();
        UpdateControls();
    }
    private void PopulatePickers()
    {
        DevicePicker.ItemsSource = ViewModel.Devices;
        DevicePicker.SelectedItem = ViewModel.Devices.FirstOrDefault(d => d.DeviceId == ViewModel.Settings.DeviceId);
        TemperaturePicker.ItemsSource = ViewModel.Temperatures;
        TemperaturePicker.SelectedItem = ViewModel.Temperatures.FirstOrDefault(s => s.Id == ViewModel.Settings.TemperatureSensorId);
        FanPicker.ItemsSource = ViewModel.Fans;
        FanPicker.SelectedItem = ViewModel.Fans.FirstOrDefault(s => s.Id == ViewModel.Settings.FanSensorId);
    }
    private bool ReadSettings()
    {
        if (demo || ViewModel.IsRunning) return false;
        if (!double.IsFinite(TemperatureValue.Value) || !double.IsFinite(FanValue.Value) || TemperatureValue.Value != Math.Truncate(TemperatureValue.Value) || FanValue.Value != Math.Truncate(FanValue.Value) || TemperatureValue.Value is < 0 or > 255 || FanValue.Value is < 0 or > 65535)
        { AuthorizationStatus.Text = "请输入有效整数：温度 0～255 ℃，转速 0～65535 RPM。"; return false; }
        ViewModel.Settings = ViewModel.Settings with { AutoStart = AutoStartMode.IsChecked == true, DeviceId = (DevicePicker.SelectedItem as CoolingDevice)?.DeviceId ?? ViewModel.Settings.DeviceId, TemperatureSensorId = (TemperaturePicker.SelectedItem as CoolingSensor)?.Id ?? ViewModel.Settings.TemperatureSensorId, FanSensorId = (FanPicker.SelectedItem as CoolingSensor)?.Id ?? ViewModel.Settings.FanSensorId, ManualValues = ManualMode.IsChecked == true, ManualTemperature = (int)TemperatureValue.Value, ManualFanRpm = (int)FanValue.Value };
        return true;
    }
    private void Start_Click(object sender, RoutedEventArgs args) { if (ReadSettings()) ViewModel.Start(); }
    private void Stop_Click(object sender, RoutedEventArgs args) { ViewModel.Deactivate(); App.Services.GetRequiredService<WindowsCoolingSensorClient>().Stop(); UpdateControls(); }
    private void Save_Click(object sender, RoutedEventArgs args) { if (ReadSettings()) ViewModel.Save(); }
}
