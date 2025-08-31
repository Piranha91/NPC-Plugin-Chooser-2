using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Converts a string path to a Brush based on whether the directory exists.
    /// Returns Red if the directory does not exist; otherwise, it finds the correct brush from the theme.
    /// </summary>
    [ValueConversion(typeof(string), typeof(Brush))]
    public class PathExistenceToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string? path = value as string;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return Brushes.Red;
            }

            // If the path exists, actively find the correct Foreground brush from the application's
            // currently loaded theme resources. This is more robust than relying on style inheritance.
            return Application.Current.TryFindResource("PrimaryForeground") as Brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}