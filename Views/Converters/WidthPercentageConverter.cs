using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Ensure this namespace matches your project
{
    public class WidthPercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width)
            {
                // Default to 100% if no parameter is provided
                double percentage = 100.0; 

                // Try to parse the parameter as a double
                if (parameter is string paramString && double.TryParse(paramString, out double parsedPercentage))
                {
                    percentage = parsedPercentage;
                }

                // Calculate the new width based on the percentage
                return Math.Max(0, width * (percentage / 100.0));
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}