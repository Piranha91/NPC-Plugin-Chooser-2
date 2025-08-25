namespace NPC_Plugin_Chooser_2.Views;

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

public class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Visibility.Collapsed;

        string enumValue = value.ToString();
        string targetValue = parameter.ToString();

        // Return Visible if the enum value matches the parameter, otherwise Collapsed.
        return enumValue.Equals(targetValue, StringComparison.InvariantCultureIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // This converter is only for one-way bindings, so we don't need to convert back.
        throw new NotImplementedException();
    }
}