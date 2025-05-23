using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Or your Converters namespace
{
    public class LineOffsetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double dimension)
            {
                // For a line of thickness 1, to have it sit *on* the edge pixel, offset by 0.5
                // If line thickness varies, this might need to be more complex (parameter for thickness / 2)
                return dimension - 0.5;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}