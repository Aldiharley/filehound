using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FileHound.App.ViewModels;

namespace FileHound.App.Controls;

/// <summary>Attached property that renders a <see cref="ResultItem"/> name with its matched characters highlighted.</summary>
public static class HighlightText
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(ResultItem), typeof(HighlightText), new PropertyMetadata(null, OnItemChanged));

    public static ResultItem? GetItem(DependencyObject d) => (ResultItem?)d.GetValue(ItemProperty);
    public static void SetItem(DependencyObject d, ResultItem? value) => d.SetValue(ItemProperty, value);

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not ResultItem item) return;
        var name = item.Name;
        if (item.Highlights.Count == 0)
        {
            tb.Inlines.Add(new Run(name));
            return;
        }
        var fore = (Brush)tb.FindResource("PeachDeep");
        var back = (Brush)tb.FindResource("HighlightBack");
        int pos = 0;
        foreach (var (start, length) in item.Highlights)
        {
            if (start < pos || start + length > name.Length) continue;
            if (start > pos) tb.Inlines.Add(new Run(name[pos..start]));
            tb.Inlines.Add(new Run(name.Substring(start, length)) { Foreground = fore, Background = back });
            pos = start + length;
        }
        if (pos < name.Length) tb.Inlines.Add(new Run(name[pos..]));
    }
}
