// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;

namespace SysBox.App.Views;

public sealed partial class MagDockSettingsView : UserControl
{
    private static readonly string[] Levels = ["0", "1", "2", "3", "4"];
    private MagDockLightingReadout? snapshot;
    private int[] editableModes = [];
    private bool synchronizing;
    private bool readyToApply;

    public event EventHandler<MagDockLightingChange>? ApplyRequested;

    public MagDockSettingsView()
    {
        InitializeComponent();
        BrightnessPicker.ItemsSource = Levels;
        SpeedPicker.ItemsSource = Levels;
    }

    public void Render(MagDockLightingReadout? lighting, bool ready)
    {
        var resetDraft = snapshot?.EditRevision != lighting?.EditRevision;
        snapshot = lighting;
        readyToApply = ready;

        if (lighting is null)
        {
            editableModes = [];
            CurrentLighting.Text = "尚未读到灯环配置。确认充电底座已通过 USB 连接后刷新外设。";
            UpdateEditor();
            return;
        }

        var modes = lighting.EditableModes.Where(IsEditableMode).Distinct().ToArray();
        if (resetDraft || !editableModes.SequenceEqual(modes))
        {
            synchronizing = true;
            try
            {
                editableModes = modes;
                LightSwitch.IsOn = lighting.IsEnabled;
                ModePicker.ItemsSource = editableModes.Select(ModeName).ToArray();
                ModePicker.SelectedIndex = Array.IndexOf(editableModes, lighting.Mode);
                BrightnessPicker.SelectedIndex = LevelIndex(lighting.Brightness);
                SpeedPicker.SelectedIndex = LevelIndex(lighting.Speed);
                ColorBox.Text = FormatColor(lighting.ColorRgb);
            }
            finally
            {
                synchronizing = false;
            }
        }

        var description = Describe(lighting);
        CurrentLighting.Text = CanEdit ? description : $"{description}（只读）";
        UpdateEditor();
    }

    private bool CanEdit => snapshot is { EditRevision.Length: > 0, Mode: >= 0 and <= 4 } &&
                            editableModes.Length > 0;

    private int? SelectedMode => ModePicker.SelectedIndex >= 0 && ModePicker.SelectedIndex < editableModes.Length
        ? editableModes[ModePicker.SelectedIndex]
        : null;

    private void UpdateEditor()
    {
        if (synchronizing)
            return;

        var edit = CanEdit;
        Editor.Visibility = VisibleWhen(edit);
        if (!edit)
            return;

        var mode = SelectedMode;
        LightFields.Visibility = VisibleWhen(LightSwitch.IsOn);
        SpeedPicker.Visibility = VisibleWhen(mode is > 0);
        ColorBox.Visibility = VisibleWhen(mode is 0 or 1);
        LightSwitch.IsEnabled = ModePicker.IsEnabled = BrightnessPicker.IsEnabled =
            SpeedPicker.IsEnabled = ColorBox.IsEnabled = readyToApply;

        var (change, error) = GetChange();
        ApplyButton.IsEnabled = readyToApply && change is not null;
        ValidationMessage.Text = error ?? "";
        ValidationMessage.Visibility = VisibleWhen(error is not null);
    }

    private (MagDockLightingChange? Change, string? Error) GetChange()
    {
        if (!CanEdit || snapshot is not { } current)
            return default;

        bool? enabled = LightSwitch.IsOn == current.IsEnabled ? null : LightSwitch.IsOn;
        if (!LightSwitch.IsOn)
            return (enabled is null ? null : new MagDockLightingChange(IsEnabled: enabled), null);

        if (SelectedMode is not { } mode)
            return (null, "请选择可用灯效。");
        var brightness = BrightnessPicker.SelectedIndex;
        var speed = SpeedPicker.SelectedIndex;
        if (brightness < 0 || (mode > 0 && speed < 0))
            return (null, "请选择亮度和适用的速度档位。");

        uint? color = null;
        if (mode is 0 or 1)
        {
            if (!TryReadColor(ColorBox.Text, out var rgb))
                return (null, "颜色格式为 #RRGGBB。");
            if (rgb != current.ColorRgb)
                color = rgb;
        }

        var change = new MagDockLightingChange(
            IsEnabled: enabled,
            Mode: mode == current.Mode ? null : mode,
            ColorRgb: color,
            Brightness: brightness == current.Brightness ? null : brightness,
            Speed: mode == 0 || speed == current.Speed ? null : speed);
        return change is { IsEnabled: null, Mode: null, ColorRgb: null, Brightness: null, Speed: null }
            ? default : (change, null);
    }

    private void Light_Toggled(object sender, RoutedEventArgs args) => UpdateEditor();
    private void Choice_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateEditor();
    private void Color_TextChanged(object sender, TextChangedEventArgs args) => UpdateEditor();

    private void Apply_Click(object sender, RoutedEventArgs args)
    {
        if (readyToApply && GetChange().Change is { } change)
            ApplyRequested?.Invoke(this, change);
    }

    private static string Describe(MagDockLightingReadout lighting)
    {
        if (!lighting.IsEnabled)
            return "设备当前：已关闭";

        var mode = ModeName(lighting.Mode);
        var brightness = $"亮度 {lighting.Brightness} 档";
        return lighting.Mode switch
        {
            0 => $"设备当前：{mode} · {brightness} · {FormatColor(lighting.ColorRgb)}",
            1 => $"设备当前：{mode} · {brightness} · 速度 {lighting.Speed} 档 · {FormatColor(lighting.ColorRgb)}",
            2 or 3 or 4 => $"设备当前：{mode} · {brightness} · 速度 {lighting.Speed} 档",
            _ => $"设备当前：{mode}"
        };
    }

    private static string ModeName(int mode) => mode switch
    {
        0 => "常亮",
        1 => "呼吸",
        2 => "闪耀",
        3 => "色彩循环",
        4 => "流光",
        5 => "音乐律动",
        _ => $"模式 {mode}"
    };

    private static string FormatColor(uint rgb) => $"#{rgb:X6}";

    private static bool TryReadColor(string text, out uint color)
    {
        color = 0;
        return text.Length == 7 && text[0] == '#' &&
               uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out color);
    }

    private static bool IsEditableMode(int mode) => mode is >= 0 and <= 4;
    private static int LevelIndex(int value) => value is >= 0 and <= 4 ? value : -1;
    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
