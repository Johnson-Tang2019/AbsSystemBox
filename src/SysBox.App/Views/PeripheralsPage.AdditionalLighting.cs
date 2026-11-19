// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;

namespace SysBox.App.Views;

public sealed partial class PeripheralsPage
{
    private string? renderedKeyColorRevision;
    private string? renderedColorKey;

    private void DockBack_Click(object sender, RoutedEventArgs args)
    {
        viewModel.ShowOverview();
        OverviewTab.Focus(FocusState.Programmatic);
    }

    private async void MagDock_ApplyRequested(object? sender, MagDockLightingChange change)
    {
        if (!ReadyToConfigure || viewModel.SelectedDevice is not
            { Kind: PeripheralKind.ChargingDock, MagDockLighting.EditRevision: { Length: > 0 } revision } device)
            return;
        await ApplyConfigurationAsync(device,
            () => configuration.SetMagDockLightingAsync(device.Id, revision, change, CancellationToken.None),
            (current, confirmed) => current with { MagDockLighting = confirmed });
    }

    private void RenderKeyboardPerKey(PeripheralDevice device, bool deviceChanged)
    {
        var readout = device.KeyboardPerKey;
        var selected = KeyboardKeyPicker.SelectedItem as string;
        var prefix = selected is null ? "" : selected + " ";
        var hasColor = selected is not null && readout is not null && readout.Colors.ContainsKey(selected);
        var color = hasColor ? $"#{readout!.Colors[selected!]:X6}" : "";
        KeyboardKeyColor.Text = hasColor ? $"{prefix}板载颜色　{color}" : $"{prefix}板载颜色　{Unavailable}";
        KeyboardKeyAssignment.Text = $"{prefix}当前键位映射　{ReadoutPending}";
        KeyboardPerKeyStatus.Text = readout is null ? "尚未读到此键盘的逐键灯光配置。" :
            readout.EditRevision is null ? "已读到板载颜色。此连接的逐键写入仍待验证。" :
            readout.IsActive ? "当前正在使用逐键灯效。" : "板载逐键配色可在自定义灯效下显示。";

        var editable = hasColor && readout!.EditRevision is not null;
        KeyboardKeyColorEditor.Visibility = VisibilityFor(editable);
        KeyboardKeyColorInput.IsEnabled = ApplyKeyboardKeyColor.IsEnabled = editable && ReadyToConfigure;
        ActivateKeyboardCustom.Visibility = VisibilityFor(editable && !readout!.IsActive);
        ActivateKeyboardCustom.IsEnabled = editable && ReadyToConfigure;
        if (deviceChanged || renderedColorKey != selected || renderedKeyColorRevision != readout?.EditRevision)
        {
            KeyboardKeyColorInput.Text = color;
            ActivateKeyboardCustom.IsChecked = false;
            renderedColorKey = selected;
            renderedKeyColorRevision = readout?.EditRevision;
        }
    }

    private async void ApplyKeyboardKeyColor_Click(object sender, RoutedEventArgs args)
    {
        if (!ReadyToConfigure || viewModel.SelectedDevice is not { KeyboardPerKey: { EditRevision: { } revision } readout } device ||
            KeyboardKeyPicker.SelectedItem is not string key || !readout.Colors.TryGetValue(key, out var currentColor))
            return;
        var text = KeyboardKeyColorInput.Text.Trim();
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1),
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            SetConfigurationStatus(device.Id, "请输入 #RRGGBB 格式的颜色。", InfoBarSeverity.Warning);
            return;
        }

        var activate = ActivateKeyboardCustom.IsChecked == true;
        if (rgb == currentColor && (!activate || readout.IsActive))
            return;

        await ApplyConfigurationAsync(device,
            () => configuration.SetKeyboardKeyColorAsync(device.Id, revision,
                new KeyboardKeyColorChange(key, rgb, activate), CancellationToken.None),
            (current, confirmed) => current with { KeyboardPerKey = confirmed }, refreshAfterSuccess: activate);
    }
}
