// Views/Converters/ModKeyToStringConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Views // Or NPC_Plugin_Chooser_2.Views.Converters
{
    [ValueConversion(typeof(ModKey), typeof(string))]
    public class ModKeyToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ModKey modKey)
            {
                return modKey.FileName; // Or modKey.ToString() if you prefer "FileName (Type)"
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // Not typically needed for one-way display
            throw new NotImplementedException();
        }
    }
}