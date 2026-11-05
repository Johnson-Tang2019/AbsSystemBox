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
    private readonly DispatcherTimer collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private BallRectangle? currentBounds;
    private int inputWidth, inputHeight;
    private double scale = 1, usage;
    private bool dragged, applying;
    private readonly FluentBallState state = new();
    private double cx => state.AnchorX;
    private double cy => state.AnchorY;
    private BallEdge edge => state.Edge;
    private bool collapsed => state.Collapsed;
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
        state.Restore(prefs.BallX, prefs.BallY, scale, new(area.X, area.Y, area.Width, area.Height));
        Ball.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        click.Tick += (_, _) => { click.Stop(); App.Desktop.ShowMain(); };
        collapseDelay.Tick += (_, _) => CollapseWhenOutside();
        Ball.Loaded += (_, _) => { Ball.XamlRoot.Changed += Root_Changed; Apply(); _ = RefreshAsync(); };
        Closed += (_, _) => { click.Stop(); collapseDelay.Stop(); cancellation.Cancel(); if (Ball.XamlRoot is { } root) root.Changed -= Root_Changed; compact.Dispose(); };
        AppWindow.Closing += (_, _) => SavePosition();
        Apply();
    }
    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(scale - sender.RasterizationScale) > 0.01)
        {
            scale = sender.RasterizationScale;
            var area = DisplayArea.GetFromPoint(new((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;
            state.Reflow(scale, new(area.X, area.Y, area.Width, area.Height));
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
            var frame = FloatingBallLayout.Frame(cx, cy, edge, collapsed, scale, area.X, area.Y, area.Width, area.Height);
            var bounds = frame.Window;
            currentBounds = bounds;
            FullBall.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            EdgeBar.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            compact.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (inputWidth != bounds.Width || inputHeight != bounds.Height)
            {
                var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
                input.ClearRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption);
                input.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [new(0, 0, bounds.Width, bounds.Height)]);
                inputWidth = bounds.Width;
                inputHeight = bounds.Height;
            }
            compact.Clip(frame.Region.X, frame.Region.Y, frame.Region.Width, frame.Region.Height, !collapsed);
            EdgeBar.Width = frame.Region.Width / scale;
            EdgeBar.Height = frame.Region.Height / scale;
            EdgeBar.HorizontalAlignment = edge == BallEdge.Left ? HorizontalAlignment.Left : edge == BallEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            EdgeBar.VerticalAlignment = edge == BallEdge.Top ? VerticalAlignment.Top : edge == BallEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            UpdateBar();
#if DEBUG
            var directory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
            if (directory is not null)
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "floating-layout.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        scale,
                        desired = bounds,
                        region = frame.Region,
                        edge = edge.ToString(),
                        collapsed,
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
                    UpdateBar();
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
        TracePointer("press:" + args.GetCurrentPoint(Ball).Properties.IsLeftButtonPressed);
        if (!args.GetCurrentPoint(Ball).Properties.IsLeftButtonPressed)
            return;
        pressed = CompactWindow.Cursor();
        collapseDelay.Stop();
        startX = currentBounds is { } bounds ? bounds.X + bounds.Width / 2.0 : cx;
        startY = currentBounds is { } rectangle ? rectangle.Y + rectangle.Height / 2.0 : cy;
        dragged = false;
        Ball.CapturePointer(args.Pointer);
    }
    private void Ball_Moved(object sender, PointerRoutedEventArgs args)
    {
        if (pressed is not null)
            TracePointer("move");
        if (pressed is not { } start)
            return;
        var now = CompactWindow.Cursor();
        var dx = now.X - start.X;
        var dy = now.Y - start.Y;
        if (dragged || Math.Sqrt((double)dx * dx + (double)dy * dy) >= 4 * scale)
        {
            dragged = true;
            click.Stop();
            state.Move(startX + dx, startY + dy);
            Apply();
        }
    }
    private void Ball_Released(object sender, PointerRoutedEventArgs args)
    {
        TracePointer("release");
        if (pressed is null)
            return;
        pressed = null;
        Ball.ReleasePointerCapture(args.Pointer);
        if (dragged)
        {
            var area = DisplayArea.GetFromPoint(new((int)cx, (int)cy), DisplayAreaFallback.Nearest).WorkArea;
            state.Commit(scale, new(area.X, area.Y, area.Width, area.Height));
            Apply();
            SavePosition();
            if (edge != BallEdge.None)
                collapseDelay.Start();
            return;
        }
        if (click.IsEnabled)
        {
            click.Stop();
            App.MainWindow.RequestMemoryCleanup();
        }
        else
            click.Start();
    }
    private void Ball_CaptureLost(object sender, PointerRoutedEventArgs args)
    {
        TracePointer("capture-lost");
        pressed = null;
        if (edge != BallEdge.None)
            collapseDelay.Start();
    }
    private void Ball_Entered(object sender, PointerRoutedEventArgs args)
    {
        TracePointer("enter");
        collapseDelay.Stop();
        if (edge != BallEdge.None && collapsed)
        {
            state.Hover(true);
            Apply();
        }
    }
    private void Ball_Exited(object sender, PointerRoutedEventArgs args)
    {
        TracePointer("exit");
        if (edge != BallEdge.None && pressed is null)
            collapseDelay.Start();
    }
    private void UpdateBar()
    {
        var vertical = edge is BallEdge.Left or BallEdge.Right;
        BarFill.Width = collapsed ? (vertical ? EdgeBar.Width : EdgeBar.Width * usage / 100) : 0;
        BarFill.Height = collapsed ? (vertical ? EdgeBar.Height * usage / 100 : EdgeBar.Height) : 0;
    }
    private void CollapseWhenOutside()
    {
        if (edge == BallEdge.None || collapsed || pressed is not null || cancellation.IsCancellationRequested)
        {
            collapseDelay.Stop();
            return;
        }
        var cursor = CompactWindow.Cursor();
        // A restored circle excludes its corners; keep it open while the cursor is in its bounding box.
        if (currentBounds is { } bounds && cursor.X >= bounds.X - 2 && cursor.X < bounds.X + bounds.Width + 2 &&
            cursor.Y >= bounds.Y - 2 && cursor.Y < bounds.Y + bounds.Height + 2)
            return;
        collapseDelay.Stop();
        state.Hover(false);
        Apply();
    }
    private void Ball_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        click.Stop();
        App.Desktop.ShowMain();
        args.Handled = true;
    }
    private void SavePosition() => App.Desktop.SaveBallPosition((int)(cx - 32 * scale), (int)(cy - 32 * scale));
    [System.Diagnostics.Conditional("DEBUG")]
    private void TracePointer(string action)
    {
#if DEBUG
        var directory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
        if (directory is not null)
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "ball-input.txt"), $"{action} cursor={CompactWindow.Cursor()} pressed={pressed} edge={edge} collapsed={collapsed}\n");
            }
            catch (System.IO.IOException) { }
#endif
    }
}
