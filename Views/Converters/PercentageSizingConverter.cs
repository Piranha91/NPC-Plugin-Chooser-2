using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Or your Converters namespace
{
    public class PercentageSizingConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double parentDimension && parentDimension > 0)
            {
                double percentageFactor = 0.1; // Default to 10%

                if (parameter is string paramString && 
                    double.TryParse(paramString, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedPercentage))
                {
                    percentageFactor = parsedPercentage / 100.0;
                }
                else if (parameter is double paramDouble) // Allow direct double like 0.1
                {
                    percentageFactor = paramDouble;
                }
                // Ensure a minimum size for very small images, otherwise checkbox might become invisible
                double calculatedSize = parentDimension * percentageFactor;
                return Math.Max(5.0, calculatedSize); // Ensure at least 5 DIPs, adjust as needed
            }
            return 10.0; // Fallback size if conversion fails or parent is 0
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}