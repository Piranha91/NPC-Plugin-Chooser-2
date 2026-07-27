using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Picks between two Thickness values on a bool. Used by the collapsible group boxes in
/// the NPCs view to zero a GroupBox's BorderThickness/Padding while it is collapsed, so
/// nothing but the clickable caption remains.
///
/// This is bound per-GroupBox rather than applied through a Style with
/// BasedOn="{StaticResource {x:Type GroupBox}}": ThemeManager swaps the theme dictionary
/// in Application.Current.Resources at runtime, and a StaticResource BasedOn would freeze
/// the GroupBox template to whichever theme happened to be loaded at parse time.
/// </summary>
[ValueConversion(typeof(bool), typeof(Thickness))]
public class BooleanToThicknessConverter : IValueConverter
{
    public Thickness TrueThickness { get; set; } = new(1);
    public Thickness FalseThickness { get; set; } = new(0);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        return b ? TrueThickness : FalseThickness;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
