using System;
using System.Globalization;
using System.Windows; // Required for Visibility enum
using System.Windows.Data; // Required for IValueConverter

namespace NPC_Plugin_Chooser_2.Views // Adjust namespace if needed
{
    /// <summary>
    /// Converts a Boolean value to a Visibility value.
    /// Default: true -> Visible, false -> Collapsed.
    /// Parameter "Invert": true -> Collapsed, false -> Visible.
    /// Parameter "Hidden": Uses Visibility.Hidden instead of Collapsed for false state.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = false;
            if (value is bool b)
            {
                boolValue = b;
            }
            else if (value is bool?) // Also handle nullable boolean
            {
                boolValue = ((bool?)value).GetValueOrDefault(); // Treat null as false
            }

            // Check for inversion parameter
            bool invert = parameter is string paramString && paramString.Equals("Invert", StringComparison.OrdinalIgnoreCase);
            if (invert)
            {
                boolValue = !boolValue;
            }

             // Determine the visibility state for 'false'
             Visibility falseVisibility = Visibility.Collapsed; // Default
             if (parameter is string paramStringHidden && paramStringHidden.Equals("Hidden", StringComparison.OrdinalIgnoreCase))
             {
                 falseVisibility = Visibility.Hidden;
             }


            return boolValue ? Visibility.Visible : falseVisibility;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // ConvertBack is typically not needed for visibility bindings
            if (value is Visibility visibility)
            {
                bool invert = parameter is string paramString && paramString.Equals("Invert", StringComparison.OrdinalIgnoreCase);
                bool result = (visibility == Visibility.Visible);
                return invert ? !result : result;
            }
            return DependencyProperty.UnsetValue; // Indicate conversion failure
        }
    }
}