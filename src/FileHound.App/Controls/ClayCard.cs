using System.Windows;
using System.Windows.Controls;

namespace FileHound.App.Controls;

/// <summary>
/// Rounded pastel card with a soft clay shadow. The shadow lives on a separate background layer so the
/// content itself is never rendered through an Effect (keeps text crisp and scrolling fast).
/// </summary>
public class ClayCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(ClayCard), new PropertyMetadata(new CornerRadius(22)));

    public static readonly DependencyProperty HasShadowProperty =
        DependencyProperty.Register(nameof(HasShadow), typeof(bool), typeof(ClayCard), new PropertyMetadata(true));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool HasShadow
    {
        get => (bool)GetValue(HasShadowProperty);
        set => SetValue(HasShadowProperty, value);
    }
}
