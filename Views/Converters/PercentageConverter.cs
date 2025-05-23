using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Adjust namespace
{
    public class PercentageConverter : IValueConverter
    {
        public double Percentage { get; set; } = 1.0; // Default to 100%

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double parentWidth)
            {
                return parentWidth * Percentage;
            }
            return double.PositiveInfinity; // Or some other fallback
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}