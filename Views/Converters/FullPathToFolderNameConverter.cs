using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views
{
    public class FullPathToFolderNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
            {
                // This correctly returns the last folder/file name from a path.
                return Path.GetFileName(path);
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // This method is not needed for this functionality.
            throw new NotImplementedException();
        }
    }
}