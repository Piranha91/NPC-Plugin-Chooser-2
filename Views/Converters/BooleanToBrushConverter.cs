using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views // Or Converters namespace
{
    public class BooleanToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Brush trueBrush = Brushes.LimeGreen; // Default True brush
            Brush falseBrush = Brushes.Transparent; // Default False brush

            if (parameter is string paramString)
            {
                var parts = paramString.Split('|');
                if (parts.Length >= 1)
                {
                    try { trueBrush = (Brush)new BrushConverter().ConvertFromString(parts[0]); } catch { }
                }
                if (parts.Length >= 2)
                {
                    try { falseBrush = (Brush)new BrushConverter().ConvertFromString(parts[1]); } catch { }
                }
            }

            return (value is bool b && b) ? trueBrush : falseBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}