// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Abstractions;
using SysBox.Core;
using SysBox.Native;
using Windows.Graphics;
namespace SysBox.App;

public sealed partial class FloatingBallWindow : Window
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly CompactWindow compact;
    private readonly DispatcherTimer click = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private double cx, cy, scale = 1, usage; private BallEdge edge; private bool collapsed, dragged, menuOpen, applying;
    private (int X, int Y)? pressed; private double startX, startY;
    public FloatingBallWindow()
    {
        InitializeComponent();
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        compact = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        scale = ((FrameworkElement)App.MainWindow.Content).XamlRoot?.RasterizationScale ?? 1;
        var area = DisplayArea.GetFromWindowId(App.MainWindow.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var prefs = App.Desktop.Preferences;
        cx = (prefs.BallX ?? area.X + area.Width - 100) + 32 * scale;
        cy = (prefs.BallY ?? area.Y + 100) + 32 * scale;
        Ball.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        click.Tick += (_, _) => { click.Stop(); App.MainWindow.RequestMemoryCleanup(); };
        Ball.Loaded += (_, _) => { Ball.XamlRoot.Changed += Root_Changed; Apply(); _ = RefreshAsync(); };
        Closed += (_, _) => { click.Stop(); cancellation.Cancel(); if (Ball.XamlRoot is { } root) root.Changed -= Root_Changed; compact.Dispose(); };
        AppWindow.Closing += (_, _) => SavePosition();
        Apply();
    }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(scale - sender.RasterizationScale) > 0.01)
        {
            scale = sender.RasterizationScale;
            if (applying)
                DispatcherQueue.TryEnqueue(Apply);
            else
                Apply();
        }
    }
    private void Apply()
    {
        if (applying)
            return;
        applying = true;
        try
        {
            var area = DisplayArea.GetFromPoint(new((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;
            var bounds = FloatingBallLayout.Arrange(cx, cy, edge, collapsed, scale, area.X, area.Y, area.Width, area.Height);
            FullBall.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            EdgeBar.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            compact.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            compact.Clip(bounds.Width, bounds.Height, !collapsed);
            if (edge == BallEdge.None)
            {
                cx = bounds.X + bounds.Width / 2.0;
                cy = bounds.Y + bounds.Height / 2.0;
            }
            var vertical = edge is BallEdge.Left or BallEdge.Right;
            BarFill.Width = collapsed ? (vertical ? 14 : 56 * usage / 100) : 0;
            BarFill.Height = collapsed ? (vertical ? 56 * usage / 100 : 14) : 0;
#if DEBUG
            var directory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
            if (directory is not null)
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "floating-layout.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        scale,
                        desired = bounds,
                        actualWidth = AppWindow.Size.Width,
                        actualHeight = AppWindow.Size.Height,
                        xamlWidth = Ball.ActualWidth,
                        xamlHeight = Ball.ActualHeight,
                        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this).ToInt64()
                    }));
                }
                catch (System.IO.IOException) { }
#endif
        }
        finally { applying = false; }
    }
    private async Task RefreshAsync()
    {
        try
        {
            var monitor = App.Services.GetRequiredService<IMemoryMonitor>();
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var result = await monitor.ReadAsync(cancellation.Token);
                    if (cancellation.IsCancellationRequested)
                        break;
                    usage = result.IsSuccess && result.Value.TotalPhysicalBytes > 0 ? Math.Clamp(100.0 * (result.Value.TotalPhysicalBytes - result.Value.AvailablePhysicalBytes) / result.Value.TotalPhysicalBytes, 0, 100) : 0;
                    Percent.Text = result.IsSuccess ? $"{usage:F0}%" : "—";
                    Ring.Value = usage;
                    Apply();
                }
                catch (OperationCanceledException) { throw; }
                catch { Percent.Text = "—"; }
                await Task.Delay(1500, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    private void Ball_Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (!args.GetCurrentPoint(Ball).Properties.IsLeftButtonPressed)
            return;
        pressed = CompactWindow.Cursor();
        startX = cx;
        startY = cy;
        dragged = false;
        Ball.CapturePointer(args.Pointer);
    }
    private void Ball_Moved(object sender, PointerRoutedEventArgs args)
    {
        if (pressed is not { } start)
            return;
        var now = CompactWindow.Cursor();
        var dx = now.X - start.X;
        var dy = now.Y - start.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 4 * scale)
        {
            dragged = true;
            click.Stop();
            edge = BallEdge.None;
            collapsed = false;
            cx = startX + dx;
            cy = startY + dy;
            Apply();
        }
    }
    private void Ball_Released(object sender, PointerRoutedEventArgs args)
    {
        if (pressed is null)
            return;
        pressed = null;
        Ball.ReleasePointerCapture(args.Pointer);
        if (dragged)
        {
            var area = DisplayArea.GetFromPoint(new((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;
            edge = FloatingBallLayout.Detect(cx, cy, scale, area.X, area.Y, area.Width, area.Height);
            Apply();
            SavePosition();
            return;
        }
        if (click.IsEnabled)
        {
            click.Stop();
            App.Desktop.ShowMain();
        }
        else
            click.Start();
    }
    private void Ball_CaptureLost(object sender, PointerRoutedEventArgs args)
    {
        pressed = null;
    }
    private void Ball_Entered(object sender, PointerRoutedEventArgs args)
    {
        if (edge != BallEdge.None && collapsed)
        {
            collapsed = false;
            Apply();
        }
    }
    private void Ball_Exited(object sender, PointerRoutedEventArgs args)
    {
        if (edge != BallEdge.None && pressed is null && !menuOpen)
        {
            collapsed = true;
            Apply();
        }
    }
    private void Menu_Opening(object sender, object args)
    {
        menuOpen = true;
        click.Stop();
    }
    private void Menu_Closed(object sender, object args)
    {
        menuOpen = false;
    }
    private void SavePosition() => App.Desktop.SaveBallPosition((int)(cx - 32 * scale), (int)(cy - 32 * scale));
    private void Clean_Click(object sender, RoutedEventArgs args) => App.MainWindow.RequestMemoryCleanup();
    private void Main_Click(object sender, RoutedEventArgs args) => App.Desktop.ShowMain();
    private void Search_Click(object sender, RoutedEventArgs args) => App.Desktop.ShowSearch();
    private void Hide_Click(object sender, RoutedEventArgs args) => App.Desktop.SetBall(false);
    private void Exit_Click(object sender, RoutedEventArgs args) => App.Desktop.Exit();
}
