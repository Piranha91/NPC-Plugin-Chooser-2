// Add this class, e.g., in your Views folder or a Converters subfolder
using System;
using System.Globalization;
using System.Windows.Data;

using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views 
{
    [ValueConversion(typeof(bool), typeof(bool))]
    public class BooleanNegationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Check if the value is actually a bool
            if (value is bool boolValue)
            {
                // If it is, return its opposite
                return !boolValue;
            }
            // For any other type (including null), return false
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // The logic is identical for converting back
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }
}