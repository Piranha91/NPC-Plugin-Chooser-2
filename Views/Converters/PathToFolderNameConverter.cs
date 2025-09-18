using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views
{
    public class PathToFolderNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                try
                {
                    // Trim trailing slashes to ensure Path.GetFileName works correctly for directories
                    return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
                catch
                {
                    return path; // Return original path on error
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}