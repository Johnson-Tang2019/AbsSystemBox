// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SysBox.App.Controls;

public sealed partial class FeaturePlaceholder : UserControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(FeaturePlaceholder), new PropertyMetadata("", OnDescriptionChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(FeaturePlaceholder), new PropertyMetadata("\uE80F", OnGlyphChanged));
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public FeaturePlaceholder() => InitializeComponent();
    private static void OnDescriptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((FeaturePlaceholder)sender).DescriptionText.Text = (string)args.NewValue;
    private static void OnGlyphChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((FeaturePlaceholder)sender).FeatureIcon.Glyph = (string)args.NewValue;
}
