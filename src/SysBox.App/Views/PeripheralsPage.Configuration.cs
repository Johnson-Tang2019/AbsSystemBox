// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;

namespace SysBox.App.Views;

public sealed partial class PeripheralsPage
{
    private static readonly string[] PaletteNames = ["红色", "绿色", "蓝色", "黄色", "品红", "青色", "白色", "多彩"];
    private readonly IPeripheralConfiguration configuration = App.Services.GetRequiredService<IPeripheralConfiguration>();
    private bool configurationBusy;
    private string? configurationDeviceId;
    private string configurationMessage = "";
    private InfoBarSeverity configurationSeverity;
    private string? renderedMouseRevision;
    private int renderedDpiStage = -1;
    private string? renderedKeyboardRevision;

    private static string EffectName(int id) => id switch
    {
        0 => "关闭灯光",
        1 => "常亮",
        2 => "呼吸",
        3 => "彩虹",
        4 => "触发",
        5 => "雨滴",
        7 => "波浪",
        8 => "星光",
        10 => "流光",
        11 => "霓虹流",
        12 => "反应",
        13 => "正弦波",
        15 => "风车",
        16 => "瀑布",
        17 => "绽放",
        _ => $"模式 {id}"
    };

    private bool ReadyToConfigure => active && !isDemo && !configurationBusy && !viewModel.IsRefreshing;

    private void RenderMouseDpiEditor(MousePerformanceReadout? performance, bool deviceChanged)
    {
        var stage = MouseDpiPicker.SelectedIndex;
        var supported = performance is { EditRevision: not null } && stage >= 0 && stage < performance.DpiStages.Count;
        MouseDpiEditor.Visibility = VisibilityFor(supported);
        MouseDpiEditor.IsHitTestVisible = ReadyToConfigure;
        MouseDpiValue.IsEnabled = ApplyMouseDpi.IsEnabled = supported && ReadyToConfigure;
        if (deviceChanged || renderedMouseRevision != performance?.EditRevision || renderedDpiStage != stage)
            MouseDpiValue.Value = supported ? performance!.DpiStages[stage] : double.NaN;
        renderedMouseRevision = performance?.EditRevision;
        renderedDpiStage = stage;
    }

    private void MouseDpi_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (active && !settingSelection)
            RenderMouseDpiEditor(viewModel.SelectedDevice?.MouseReadout?.Performance, deviceChanged: false);
    }

    private async void ApplyMouseDpi_Click(object sender, RoutedEventArgs args)
    {
        var stage = MouseDpiPicker.SelectedIndex;
        if (!ReadyToConfigure || viewModel.SelectedDevice is not { MouseReadout.Performance: { EditRevision: { } revision } performance } device ||
            stage < 0 || stage >= performance.DpiStages.Count)
            return;
        var value = MouseDpiValue.Value;
        if (!(value is >= 50 and <= 42000) || value != Math.Truncate(value) || value % 50 != 0)
        {
            SetConfigurationStatus(device.Id, "请输入 50 到 42000 之间的 DPI，并以 50 为步长。", InfoBarSeverity.Warning);
            return;
        }
        if ((int)value == performance.DpiStages[stage])
            return;

        await ApplyConfigurationAsync(device, () => configuration.SetMouseDpiAsync(
                device.Id, revision, stage, (int)value, CancellationToken.None),
            (current, confirmed) => current with { MouseReadout = current.MouseReadout! with { Performance = confirmed } });
    }

    private void RenderKeyboardLightingEditor(KeyboardLightingReadout? lighting, bool deviceChanged)
    {
        var supported = lighting is { EditRevision: not null, EditableEffects.Count: > 0 };
        KeyboardLightingEditor.Visibility = VisibilityFor(supported);
        KeyboardEffectPicker.IsEnabled = ApplyKeyboardLighting.IsEnabled = supported && ReadyToConfigure;
        if (deviceChanged || renderedKeyboardRevision != lighting?.EditRevision)
        {
            var effects = supported
                ? lighting!.EditableEffects!.Select(id => new LightingEffectChoice(id, EffectName(id))).ToArray()
                : [];
            KeyboardEffectPicker.ItemsSource = effects;
            KeyboardEffectPicker.SelectedItem = effects.FirstOrDefault(effect => effect.Id == lighting?.EffectId);
            KeyboardPalettePicker.ItemsSource = PaletteNames;
            RenderKeyboardLightingFields(lighting, reset: true);
        }
        else
            RenderKeyboardLightingFields(lighting, reset: false);
        renderedKeyboardRevision = lighting?.EditRevision;
    }

    private void RenderKeyboardLightingFields(KeyboardLightingReadout? lighting, bool reset)
    {
        var editCurrentEffect = lighting is { EditRevision: not null } &&
            KeyboardEffectPicker.SelectedItem is LightingEffectChoice selected && selected.Id == lighting.EffectId;
        KeyboardBrightnessValue.IsEnabled = ReadyToConfigure && editCurrentEffect && lighting?.BrightnessLevel is not null;
        KeyboardSpeedValue.IsEnabled = ReadyToConfigure && editCurrentEffect && lighting?.SpeedLevel is not null;
        KeyboardPalettePicker.IsEnabled = ReadyToConfigure && editCurrentEffect && lighting?.PaletteIndex is not null;
        if (!reset)
            return;
        KeyboardBrightnessValue.Value = editCurrentEffect ? lighting?.BrightnessLevel ?? double.NaN : double.NaN;
        KeyboardSpeedValue.Value = editCurrentEffect ? lighting?.SpeedLevel ?? double.NaN : double.NaN;
        KeyboardPalettePicker.SelectedIndex = editCurrentEffect ? lighting?.PaletteIndex ?? -1 : -1;
    }

    private void KeyboardEffect_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (active && !settingSelection)
            RenderKeyboardLightingFields(viewModel.SelectedDevice?.KeyboardLighting, reset: true);
    }

    private async void ApplyKeyboardLighting_Click(object sender, RoutedEventArgs args)
    {
        if (!ReadyToConfigure || viewModel.SelectedDevice is not { KeyboardLighting: { EditRevision: { } revision } lighting } device ||
            KeyboardEffectPicker.SelectedItem is not LightingEffectChoice effect)
            return;
        if ((KeyboardBrightnessValue.IsEnabled && !ValidLightLevel(KeyboardBrightnessValue.Value)) ||
            (KeyboardSpeedValue.IsEnabled && !ValidLightLevel(KeyboardSpeedValue.Value)))
        {
            SetConfigurationStatus(device.Id, "亮度和速度使用 0 到 4 的整数档位。", InfoBarSeverity.Warning);
            return;
        }
        var change = effect.Id != lighting.EffectId ? new KeyboardLightingChange(effect.Id) :
            new KeyboardLightingChange(effect.Id,
                ChangedLevel(KeyboardBrightnessValue.Value, lighting.BrightnessLevel),
                ChangedLevel(KeyboardSpeedValue.Value, lighting.SpeedLevel),
                PaletteIndex: KeyboardPalettePicker.IsEnabled && KeyboardPalettePicker.SelectedIndex != lighting.PaletteIndex
                    ? KeyboardPalettePicker.SelectedIndex : null);
        if (change.EffectId == lighting.EffectId && change.BrightnessLevel is null &&
            change.SpeedLevel is null && change.PaletteIndex is null)
            return;
        await ApplyConfigurationAsync(device, () => configuration.SetKeyboardLightingAsync(
                device.Id, revision, change, CancellationToken.None),
            (current, confirmed) => current with { KeyboardLighting = confirmed });
    }

    private static int? ChangedLevel(double value, int? original) =>
        original is not null && ValidLightLevel(value) && value != original
            ? (int)value : null;

    private static bool ValidLightLevel(double value) => value is >= 0 and <= 4 && value == Math.Truncate(value);

    private async Task ApplyConfigurationAsync<T>(PeripheralDevice device, Func<Task<Result<T>>> apply,
        Func<PeripheralDevice, T, PeripheralDevice> update) where T : notnull
    {
        configurationBusy = true;
        SetConfigurationStatus(device.Id, "正在应用并回读设备设置…", InfoBarSeverity.Informational);
        try
        {
            var result = await apply();
            if (result.IsSuccess)
                viewModel.UpdateConfirmedDevice(update(device, result.Value));
            else if (active)
                await viewModel.RefreshAsync();
            SetConfigurationStatus(device.Id, result.IsSuccess ? "已回读确认设备设置。" : result.Error!.Message,
                result.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            configurationBusy = false;
            if (active)
                Render();
        }
    }

    private void SetConfigurationStatus(string deviceId, string message, InfoBarSeverity severity)
    {
        configurationDeviceId = deviceId;
        configurationMessage = message;
        configurationSeverity = severity;
        if (active)
            Render();
    }

    private void ShowConfigurationStatus(string? deviceId)
    {
        ConfigurationStatus.IsOpen = deviceId is not null && deviceId == configurationDeviceId;
        ConfigurationStatus.Title = configurationMessage;
        ConfigurationStatus.Severity = configurationSeverity;
    }

    public sealed record LightingEffectChoice(int Id, string Name);
}
