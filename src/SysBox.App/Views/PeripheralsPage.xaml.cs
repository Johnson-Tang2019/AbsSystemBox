// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Presentation;

namespace SysBox.App.Views;

public sealed partial class PeripheralsPage : Page, IPageLifetime
{
    private readonly PeripheralsViewModel viewModel = App.Services.GetRequiredService<PeripheralsViewModel>();
    private readonly bool isDemo = App.Services.GetRequiredService<IDashboardClient>().IsDemo;
    private IReadOnlyList<PeripheralDevice> displayedDevices = [];
    private PeripheralListItem[] mouseItems = [];
    private PeripheralListItem[] keyboardItems = [];
    private PeripheralListItem[] gamepadItems = [];
    private bool active;
    private bool renderPending;
    private bool settingSelection;
    private PeripheralKind? displayedKind;
    private string? displayedSettingsId;

    public PeripheralsPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += (_, _) => Deactivate();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (active)
            return;

        active = true;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (isDemo)
        {
            RefreshButton.Visibility = Visibility.Collapsed;
            CategorySelector.Visibility = Visibility.Collapsed;
            ShowNotice(PageNotice, "示例预览不读取本机外设。请在普通模式打开此页面。");
            return;
        }

        Render();
        await viewModel.RefreshAsync();
    }

    public void Deactivate()
    {
        if (!active)
            return;

        active = false;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.Deactivate();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        if (active && !isDemo)
            await viewModel.RefreshAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!active || renderPending)
            return;

        renderPending = DispatcherQueue.TryEnqueue(() =>
        {
            renderPending = false;
            if (active)
                Render();
        });
    }

    private void Category_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!active || settingSelection)
            return;

        if (sender.SelectedItem == MouseTab)
            viewModel.ShowKind(PeripheralKind.Mouse);
        else if (sender.SelectedItem == KeyboardTab)
            viewModel.ShowKind(PeripheralKind.Keyboard);
        else if (sender.SelectedItem == GamepadTab)
            viewModel.ShowKind(PeripheralKind.Gamepad);
        else if (sender.SelectedItem == OverviewTab)
            viewModel.ShowOverview();
    }

    private void DeviceCard_Click(object sender, RoutedEventArgs args)
    {
        if (active && sender is Button { DataContext: PeripheralListItem item })
        {
            viewModel.ShowDevice(item.Device);
            Render();
            CategorySelector.SelectedItem?.Focus(FocusState.Programmatic);
        }
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (active && !settingSelection && DevicePicker.SelectedItem is PeripheralListItem item)
            viewModel.ShowDevice(item.Device);
    }

    private void Render()
    {
        var devices = viewModel.Devices;
        var kind = viewModel.SelectedKind;
        var error = viewModel.ErrorMessage;
        var refreshing = viewModel.IsRefreshing;
        RefreshButton.IsEnabled = !refreshing;
        RefreshProgress.IsIndeterminate = refreshing;
        RefreshProgress.Visibility = VisibilityFor(refreshing);
        ErrorBar.IsOpen = error is not null;
        ErrorBar.Message = error ?? "";

        UpdateDeviceLists(devices);
        ShowNotice(PageNotice, kind is null && viewModel.HasLoaded && devices.Count == 0 && error is null
            ? "未检测到鼠标、键盘或手柄。连接设备后刷新。"
            : "");

        Overview.Visibility = VisibilityFor(kind is null);
        Details.Visibility = VisibilityFor(kind is not null);

        settingSelection = true;
        try
        {
            CategorySelector.SelectedItem = kind switch
            {
                PeripheralKind.Mouse => MouseTab,
                PeripheralKind.Keyboard => KeyboardTab,
                PeripheralKind.Gamepad => GamepadTab,
                _ => OverviewTab
            };
            if (kind is { } selectedKind)
                UpdateDetails(selectedKind);
        }
        finally
        {
            settingSelection = false;
        }

        if (displayedKind != kind)
        {
            displayedKind = kind;
            ContentScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private void UpdateDeviceLists(IReadOnlyList<PeripheralDevice> devices)
    {
        if (displayedDevices.Count == devices.Count &&
            displayedDevices.Zip(devices).All(pair => CardFields(pair.First) == CardFields(pair.Second)))
            return;

        displayedDevices = devices;
        mouseItems = CreateItems(devices, PeripheralKind.Mouse);
        keyboardItems = CreateItems(devices, PeripheralKind.Keyboard);
        gamepadItems = CreateItems(devices, PeripheralKind.Gamepad);
        MouseDevices.ItemsSource = mouseItems;
        KeyboardDevices.ItemsSource = keyboardItems;
        GamepadDevices.ItemsSource = gamepadItems;
        MouseSection.Visibility = VisibilityFor(mouseItems.Length > 0);
        KeyboardSection.Visibility = VisibilityFor(keyboardItems.Length > 0);
        GamepadSection.Visibility = VisibilityFor(gamepadItems.Length > 0);
    }

    private static (string, PeripheralKind, string, PeripheralConnection, PeripheralPresence) CardFields(PeripheralDevice device) =>
        (device.Id, device.Kind, device.Name, device.Connection, device.Presence);

    private void UpdateDetails(PeripheralKind kind)
    {
        var items = kind switch
        {
            PeripheralKind.Mouse => mouseItems,
            PeripheralKind.Keyboard => keyboardItems,
            _ => gamepadItems
        };
        var selected = viewModel.SelectedDevice;
        var picked = items.FirstOrDefault(item => item.Device.Id == selected?.Id);
        if (!ReferenceEquals(DevicePicker.ItemsSource, items))
            DevicePicker.ItemsSource = items;
        DevicePicker.SelectedItem = picked;
        DevicePicker.Visibility = VisibilityFor(items.Length > 1 || items.Length > 0 && selected is null);
        DetailCard.Visibility = VisibilityFor(picked is not null);
        UpdateSettings(selected);

        var notice = viewModel.IsSelectedDeviceMissing
            ? $"当前未检测到已选{KindName(kind)}。可重新连接后刷新。"
            : viewModel.HasLoaded && viewModel.ErrorMessage is null && items.Length == 0
                ? $"未检测到{KindName(kind)}。连接设备后刷新。"
                : items.Length > 0 && picked is null
                    ? "选择一台设备以查看详情。"
                    : "";
        ShowNotice(DetailNotice, notice);
        if (picked is null || selected is null)
            return;

        var device = selected;
        DeviceTitle.Text = picked.Name;
        DeviceStatus.Text = PresenceName(device.Presence);
        DeviceType.Text = KindName(device.Kind);
        DeviceManufacturer.Text = device.Manufacturer ?? "未报告";
        DeviceModel.Text = device.Model ?? "未确认";
        DeviceModelEvidence.Text = device.ModelEvidence switch
        {
            PeripheralModelEvidence.SystemReported => "Windows 报告",
            PeripheralModelEvidence.CatalogMatched => "与已知设备资料匹配",
            PeripheralModelEvidence.HardwareReported => "设备身份回报",
            _ => "未确认"
        };
        DeviceConnection.Text = ConnectionName(device.Connection);
        DeviceIds.Text = $"{FormatId(device.VendorId)} / {FormatId(device.ProductId)}";
        IdentityLabel.Visibility = VisibilityFor(device.HardwareIdentityCode is not null);
        ShowNotice(DeviceIdentityCode, device.HardwareIdentityCode ?? "");
        ReceiverNameLabel.Visibility = VisibilityFor(device.ReceiverName is not null);
        ShowNotice(DeviceReceiverName, device.ReceiverName ?? "");
    }

    private const string Unavailable = "尚未读取";
    private const string ReadoutPending = "尚未接入读取";
    private static readonly string[] MouseButtons = ["左键", "右键", "滚轮中键", "前进侧键", "后退侧键"];
    private static readonly string[] KeyboardKeys = string.Join(" ", new[]
    {
        "Esc F1 F2 F3 F4 F5 F6 F7 F8 F9 F10 F11 F12 PrintScreen ScrollLock Pause",
        "` 1 2 3 4 5 6 7 8 9 0 - = Backspace Insert Home PageUp",
        "Tab Q W E R T Y U I O P [ ] \\ Delete End PageDown",
        "CapsLock A S D F G H J K L ; ' Enter",
        "左Shift Z X C V B N M , . / 右Shift ↑",
        "左Ctrl Windows 左Alt 空格 右Alt Fn 菜单 右Ctrl ← ↓ →"
    }).Split(' ');

    private void UpdateSettings(PeripheralDevice? device)
    {
        var deviceChanged = displayedSettingsId != device?.Id;
        displayedSettingsId = device?.Id;
        MouseSettings.Visibility = VisibilityFor(device is { Kind: PeripheralKind.Mouse });
        KeyboardSettings.Visibility = VisibilityFor(device is { Kind: PeripheralKind.Keyboard });
        if (device is { Kind: PeripheralKind.Mouse })
            UpdateMouseSettings(device, deviceChanged);
        else if (device is { Kind: PeripheralKind.Keyboard })
            UpdateKeyboardSettings(device, deviceChanged);
    }

    private void UpdateMouseSettings(PeripheralDevice device, bool deviceChanged)
    {
        var mouse = device.MouseReadout;
        var performance = mouse?.Performance;
        var isK7 = mouse is not null || string.Equals(device.ReceiverName, "MCHOSE K7 Ultra", StringComparison.OrdinalIgnoreCase);
        MouseReadoutStatus.Text = mouse is not null ? "当前数据来自 2.4G 鼠标本体。" :
            isK7 ? "接收器已检测到，暂未取得本体数据。移动鼠标唤醒后可刷新重试。" :
            "选择已连接的迈从 K7 Ultra 读取板载设置。";
        MouseFirmware.Text = $"固件版本　{mouse?.FirmwareVersion ?? Unavailable}";
        MouseBattery.Text = $"电量　{(mouse?.BatteryPercent is { } battery ? $"{battery}%" : Unavailable)}";
        MouseProfile.Text = $"当前板载配置　{(performance?.ActiveProfile is { } profile ? $"第 {profile + 1} 组" : Unavailable)}";
        MouseDpi.Text = $"当前 DPI　{(performance is null ? Unavailable :
            $"第 {performance.ActiveDpiStage + 1} 档，{DpiValue(performance, performance.ActiveDpiStage)}")}";
        var dpiItems = performance is null ? [] : performance.DpiStages.Select((_, index) =>
            $"第 {index + 1} 档 · {DpiValue(performance, index)}{(index == performance.ActiveDpiStage ? "（当前）" : "")}").ToArray();
        if (MouseDpiPicker.ItemsSource is not string[] previous || !previous.SequenceEqual(dpiItems))
        {
            MouseDpiPicker.ItemsSource = dpiItems;
            deviceChanged = true;
        }
        if (deviceChanged)
            MouseDpiPicker.SelectedIndex = performance?.ActiveDpiStage ?? -1;
        MouseDpiPicker.IsEnabled = dpiItems.Length > 0;
        MouseDpiPicker.PlaceholderText = Unavailable;
        MousePolling.Text = $"回报率　{WithUnit(performance?.PollingRateHz, "Hz")}";
        MouseLiftOff.Text = $"抬升高度（LOD）　{(performance?.LiftOffDistanceMm is { } height ? $"{height:G} mm" : Unavailable)}";
        MouseDebounce.Text = $"按键防抖　{WithUnit(performance?.DebounceMilliseconds, "ms")}";
        MouseSleep.Text = $"自动休眠　{(performance?.SleepMinutes == 0 ? "关闭" : WithUnit(performance?.SleepMinutes, "分钟"))}";
        SetInspector(MouseButtonPicker, MouseButtons, isK7, deviceChanged);
        UpdateMouseButton();
    }

    private static string DpiValue(MousePerformanceReadout performance, int index)
    {
        var horizontal = performance.DpiStages[index];
        var vertical = performance.VerticalDpiStages?[index];
        return vertical == horizontal ? $"{horizontal} DPI" : $"X {horizontal} / Y {vertical?.ToString() ?? Unavailable} DPI";
    }

    private void UpdateKeyboardSettings(PeripheralDevice device, bool deviceChanged)
    {
        var lighting = device.KeyboardLighting;
        var isF87Pro = device is { Model: "F87 Pro", ModelEvidence: PeripheralModelEvidence.HardwareReported };
        KeyboardReadoutStatus.Text = lighting is not null ? "当前灯光数据来自 USB 有线键盘。" :
            isF87Pro ? "已识别狼蛛 F87 Pro，暂未取得灯光数据。可刷新重试。" :
            "选择通过 USB 连接的狼蛛 F87 Pro 读取板载设置。";
        KeyboardEffect.Text = $"当前灯效　{(lighting is null ? Unavailable : $"模式 {lighting.EffectId}")}";
        KeyboardBrightness.Text = $"亮度　{WithUnit(lighting?.BrightnessLevel, "档")}";
        KeyboardSpeed.Text = $"速度　{WithUnit(lighting?.SpeedLevel, "档")}";
        KeyboardColor.Text = $"灯效颜色　{ReadoutPending}";
        SetInspector(KeyboardKeyPicker, KeyboardKeys, isF87Pro, deviceChanged);
        UpdateKeyboardKey();
    }

    private static void SetInspector(ComboBox picker, string[] items, bool available, bool deviceChanged)
    {
        var source = available ? items : null;
        if (!ReferenceEquals(picker.ItemsSource, source))
        {
            picker.ItemsSource = source;
            deviceChanged = true;
        }
        if (deviceChanged)
            picker.SelectedIndex = available ? 0 : -1;
        picker.IsEnabled = available;
    }

    private void MouseButton_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateMouseButton();

    private void UpdateMouseButton()
    {
        var selected = MouseButtonPicker.SelectedItem as string;
        MouseButtonAssignment.Text = selected is null ? $"当前按键映射　{ReadoutPending}" : $"{selected}的当前映射　{ReadoutPending}";
    }

    private void KeyboardKey_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateKeyboardKey();

    private void UpdateKeyboardKey()
    {
        var selected = KeyboardKeyPicker.SelectedItem as string;
        var key = selected is null ? "" : selected + " ";
        KeyboardKeyColor.Text = $"{key}逐键 RGB　{ReadoutPending}";
        KeyboardKeyAssignment.Text = $"{key}当前键位映射　{ReadoutPending}";
    }

    private static string WithUnit(int? value, string unit) => value is { } number ? $"{number} {unit}" : Unavailable;

    private static PeripheralListItem[] CreateItems(IReadOnlyList<PeripheralDevice> devices, PeripheralKind kind) =>
        devices.Where(device => device.Kind == kind)
            .GroupBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var sameName = group.OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
                return sameName.Select((device, index) => new PeripheralListItem(
                    device,
                    sameName.Length == 1 ? device.Name : $"{device.Name}（同名设备 {index + 1}）",
                    $"{ConnectionName(device.Connection)} · {PresenceName(device.Presence)}"));
            })
            .ToArray();

    private static void ShowNotice(TextBlock target, string text)
    {
        target.Text = text;
        target.Visibility = VisibilityFor(text.Length > 0);
    }

    private static Visibility VisibilityFor(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static string KindName(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Mouse => "鼠标",
        PeripheralKind.Keyboard => "键盘",
        PeripheralKind.Gamepad => "手柄",
        _ => "外设"
    };

    private static string ConnectionName(PeripheralConnection connection) => connection switch
    {
        PeripheralConnection.Usb => "USB",
        PeripheralConnection.WirelessReceiver => "无线接收器",
        PeripheralConnection.Bluetooth => "蓝牙",
        _ => "未知连接方式"
    };

    private static string PresenceName(PeripheralPresence presence) => presence == PeripheralPresence.ReceiverOnly
        ? "仅检测到接收器，设备连接状态未知"
        : "已检测到设备";

    private static string FormatId(ushort? id) => id is { } value ? value.ToString("X4") : "未知";
}

public sealed record PeripheralListItem(PeripheralDevice Device, string Name, string Summary)
{
    public string AccessibleName => $"{Name}，{Summary}";
}
