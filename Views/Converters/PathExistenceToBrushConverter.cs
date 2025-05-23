using System;
using System.Globalization;
using System.IO; // Required for Directory.Exists
using System.Windows; // Required for SystemColors
using System.Windows.Data;
using System.Windows.Media; // Required for Brushes

namespace NPC_Plugin_Chooser_2.Views // Or your Converters namespace
{
    /// <summary>
    /// Converts a string path to a Brush based on whether the directory exists.
    /// Returns Red if the directory does not exist, otherwise returns the system default text brush.
    /// </summary>
    [ValueConversion(typeof(string), typeof(Brush))]
    public class PathExistenceToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string? path = value as string;

            // If the path is null, empty, whitespace, or doesn't exist on disk, return Red.
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return Brushes.Red;
            }

            // If the path exists, return the default system text brush.
            return SystemColors.ControlTextBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Converting back is not supported for this converter.
            throw new NotImplementedException();
        }
    }
}