using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models; // Assuming ModKey is here or similar

namespace NPC_Plugin_Chooser_2.Views
{
    public class ResourceModKeyTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || !(values[0] is ModKey currentModKey) || !(values[1] is HashSet<ModKey> resourceKeys))
            {
                return values.Length > 0 && values[0] is ModKey key ? key.FileName.String : string.Empty;
            }

            var fileName = currentModKey.FileName.String;

            if (resourceKeys.Contains(currentModKey))
            {
                return $"(Resource Only) {fileName}";
            }
            
            return fileName;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}