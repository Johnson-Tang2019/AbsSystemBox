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
        if (displayedDevices.SequenceEqual(devices))
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

        var notice = viewModel.IsSelectedDeviceMissing
            ? $"当前未检测到已选{KindName(kind)}。可重新连接后刷新。"
            : viewModel.HasLoaded && viewModel.ErrorMessage is null && items.Length == 0
                ? $"未检测到{KindName(kind)}。连接设备后刷新。"
                : items.Length > 0 && picked is null
                    ? "选择一台设备以查看详情。"
                    : "";
        ShowNotice(DetailNotice, notice);
        if (picked is null)
            return;

        var device = picked.Device;
        DeviceTitle.Text = picked.Name;
        DeviceStatus.Text = PresenceName(device.Presence);
        DeviceType.Text = KindName(device.Kind);
        DeviceManufacturer.Text = device.Manufacturer ?? "未报告";
        DeviceModel.Text = device.Model ?? "未确认";
        DeviceModelEvidence.Text = device.ModelEvidence switch
        {
            PeripheralModelEvidence.SystemReported => "Windows 报告",
            PeripheralModelEvidence.CatalogMatched => "与已知设备资料匹配",
            _ => "未确认"
        };
        DeviceConnection.Text = ConnectionName(device.Connection);
        DeviceIds.Text = $"{FormatId(device.VendorId)} / {FormatId(device.ProductId)}";
    }

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
