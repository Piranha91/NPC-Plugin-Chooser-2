using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views;

[ValueConversion(typeof(bool), typeof(double))]
public class BooleanToOpacityConverter : IValueConverter
{
    public double TrueOpacity { get; set; } = 1.0;
    public double FalseOpacity { get; set; } = 0.45;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        return b ? TrueOpacity : FalseOpacity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => System.Windows.DependencyProperty.UnsetValue;
}
