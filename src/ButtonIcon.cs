using System.Windows;
using System.Windows.Media;

namespace CodexTraffic;

/// <summary>Decorates an action without changing its text content or automation name.</summary>
public static class ButtonIcon
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.RegisterAttached(
        "Data", typeof(Geometry), typeof(ButtonIcon), new FrameworkPropertyMetadata(null));

    public static Geometry? GetData(DependencyObject element) => (Geometry?)element.GetValue(DataProperty);
    public static void SetData(DependencyObject element, Geometry? value) => element.SetValue(DataProperty, value);
}
