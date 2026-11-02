// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Abstractions;
using SysBox.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace SysBox.App;

public sealed partial class FloatingBallWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private Point? pressed; private PointInt32 origin; private bool dragged;
    private int physicalSize = 76;
    private double scale = 1;
    public FloatingBallWindow()
    {
        InitializeComponent();
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new(76, 76));
        var area = DisplayArea.GetFromWindowId(App.MainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var preferences = App.Desktop.Preferences;
        var position = DesktopBounds.Clamp(preferences.BallX ?? area.X + area.Width - 100, preferences.BallY ?? area.Y + 100, 76, area.X, area.Y, area.Width, area.Height);
        AppWindow.Move(new(position.X, position.Y));
        SysBox.Native.DesktopShell.RoundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), 76);
        Ball.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        Ball.Loaded += (_, _) => { Ball.XamlRoot.Changed += Root_Changed; UpdateScale(); _ = RefreshAsync(); };
        AppWindow.Closing += (_, _) => App.Desktop.SaveBallPosition(AppWindow.Position.X, AppWindow.Position.Y);
        Closed += (_, _) => { if (Ball.XamlRoot is { } root) root.Changed -= Root_Changed; cancellation.Cancel(); };
    }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateScale();
    private void UpdateScale()
    {
        scale = Ball.XamlRoot.RasterizationScale;
        var next = (int)Math.Ceiling(76 * scale);
        if (next == physicalSize)
            return;
        physicalSize = next;
        AppWindow.Resize(new(next, next));
        SysBox.Native.DesktopShell.RoundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), next);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var position = DesktopBounds.Clamp(AppWindow.Position.X, AppWindow.Position.Y, next, area.X, area.Y, area.Width, area.Height);
        AppWindow.Move(new(position.X, position.Y));
    }
    private async Task RefreshAsync()
    {
        try
        {
            var monitor = App.Services.GetRequiredService<IMemoryMonitor>();
            while (!cancellation.IsCancellationRequested)
            {
                var result = await monitor.ReadAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested)
                    break;
                Percent.Text = result.IsSuccess && result.Value.TotalPhysicalBytes > 0 ? $"{100.0 * (result.Value.TotalPhysicalBytes - result.Value.AvailablePhysicalBytes) / result.Value.TotalPhysicalBytes:F0}%" : "—";
                await Task.Delay(5000, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!cancellation.IsCancellationRequested) Percent.Text = "未知"; }
    }
    private void Ball_Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(Ball).Properties.IsLeftButtonPressed)
            return;
        pressed = args.GetCurrentPoint(Ball).Position;
        origin = AppWindow.Position;
        dragged = false;
        Ball.CapturePointer(args.Pointer);
    }
    private void Ball_Moved(object sender, PointerRoutedEventArgs args)
    {
        if (pressed is not { } start)
            return;
        var point = args.GetCurrentPoint(Ball).Position;
        var x = AppWindow.Position.X + (int)((point.X - start.X) * scale);
        var y = AppWindow.Position.Y + (int)((point.Y - start.Y) * scale);
        dragged |= Math.Abs(x - origin.X) + Math.Abs(y - origin.Y) > 6;
        var area = DisplayArea.GetFromPoint(new(x, y), DisplayAreaFallback.Nearest).WorkArea;
        var position = DesktopBounds.Clamp(x, y, physicalSize, area.X, area.Y, area.Width, area.Height);
        AppWindow.Move(new(position.X, position.Y));
    }
    private void Ball_Released(object sender, PointerRoutedEventArgs args)
    {
        if (pressed is null)
            return;
        pressed = null;
        Ball.ReleasePointerCapture(args.Pointer);
        if (!dragged)
            App.Desktop.ShowSearch();
    }
    private void Main_Click(object sender, RoutedEventArgs args) => App.Desktop.ShowMain();
    private void Search_Click(object sender, RoutedEventArgs args) => App.Desktop.ShowSearch();
    private void Hide_Click(object sender, RoutedEventArgs args) => App.Desktop.SetBall(false);
    private void Exit_Click(object sender, RoutedEventArgs args) => App.Desktop.Exit();
}
