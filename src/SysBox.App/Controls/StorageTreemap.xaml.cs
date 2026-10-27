// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SysBox.Abstractions;
using SysBox.Presentation;

namespace SysBox.App.Controls;

public sealed partial class StorageTreemap : UserControl
{
    private StorageView? view;
    public event EventHandler<string>? FolderInvoked;
    public StorageTreemap() => InitializeComponent();
    public void Update(StorageView? value) { view = value; Draw(); }
    private void Tiles_SizeChanged(object sender, SizeChangedEventArgs args) => Draw();
    private void Draw()
    {
        if (Tiles is null) return;
        Tiles.Children.Clear();
        if (view is null || Tiles.ActualWidth <= 0 || Tiles.ActualHeight <= 0) return;
        var weights = view.Children.Select(x => new TreemapWeight(x.Name, x.IsDirectory ? x.Path : null, x.LogicalBytes)).ToList();
        var shown = weights.Sum(x => x.Bytes);
        if (view.Summary.LogicalBytes > shown) weights.Add(new("其他页的项目", null, view.Summary.LogicalBytes - shown));
        var index = 0;
        foreach (var tile in TreemapLayout.Create(weights, Tiles.ActualWidth, Tiles.ActualHeight))
        {
            var text = new TextBlock { Text = tile.Item.Name, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2, FontSize = 12 };
            var button = new Button { Content = text, Width = Math.Max(0, tile.Width - 2), Height = Math.Max(0, tile.Height - 2),
                Padding = new Thickness(5), CornerRadius = new CornerRadius(3), HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Top, MinWidth = 0, MinHeight = 0, Style = (Style)Resources[index++ % 2 == 0 ? "TileA" : "TileB"] };
            ToolTipService.SetToolTip(button, $"{tile.Item.Name}\n{tile.Item.Bytes:N0} 逻辑字节（已知部分）");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{tile.Item.Name}，{tile.Item.Bytes:N0} 逻辑字节");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, $"StorageTile{index - 1}");
            if (tile.Item.Path is { } path) button.Click += (_, _) => FolderInvoked?.Invoke(this, path);
            Canvas.SetLeft(button, tile.X); Canvas.SetTop(button, tile.Y); Tiles.Children.Add(button);
        }
    }
}
