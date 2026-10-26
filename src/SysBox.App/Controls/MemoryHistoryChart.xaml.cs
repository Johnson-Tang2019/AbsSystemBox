// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SysBox.Presentation;
using Windows.Foundation;

namespace SysBox.App.Controls;

public sealed partial class MemoryHistoryChart : UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<MemoryHistoryPoint>), typeof(MemoryHistoryChart),
        new PropertyMetadata(null, (sender, _) => ((MemoryHistoryChart)sender).Draw()));

    public IReadOnlyList<MemoryHistoryPoint>? Samples
    {
        get => (IReadOnlyList<MemoryHistoryPoint>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public MemoryHistoryChart() => InitializeComponent();
    private void Plot_SizeChanged(object sender, SizeChangedEventArgs args) => Draw();

    private void Draw()
    {
        if (Plot is null || Plot.ActualWidth <= 0 || Plot.ActualHeight <= 0) return;
        var width = Plot.ActualWidth - 6;
        var height = Plot.ActualHeight - 6;
        TopLine.X2 = MiddleLine.X2 = BottomLine.X2 = width + 6;
        TopLine.Y1 = TopLine.Y2 = 3;
        MiddleLine.Y1 = MiddleLine.Y2 = height / 2 + 3;
        BottomLine.Y1 = BottomLine.Y2 = height + 3;
        var points = new PointCollection();
        var samples = Samples;
        if (samples is { Count: > 0 })
        {
            var duration = (samples[^1].TimestampUtc - samples[0].TimestampUtc).TotalSeconds;
            for (var index = 0; index < samples.Count; index++)
            {
                var x = samples.Count == 1 ? width : duration > 0
                    ? width * (samples[index].TimestampUtc - samples[0].TimestampUtc).TotalSeconds / duration
                    : width * index / (samples.Count - 1);
                points.Add(new Point(x + 3, height * (1 - Math.Clamp(samples[index].UsedPercent, 0, 100) / 100) + 3));
            }
        }
        Curve.Points = points;
        var area = new PointCollection();
        if (points.Count > 0)
        {
            area.Add(new Point(points[0].X, height + 3));
            foreach (var point in points) area.Add(point);
            area.Add(new Point(points[^1].X, height + 3));
            Canvas.SetLeft(Latest, points[^1].X - 3);
            Canvas.SetTop(Latest, points[^1].Y - 3);
        }
        Area.Points = area;
        Latest.Visibility = points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
