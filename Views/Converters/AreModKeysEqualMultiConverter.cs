// Views/Converters/AreModKeysEqualMultiConverter.cs
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Views // Or NPC_Plugin_Chooser_2.Views.Converters
{
    public class AreModKeysEqualMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
                return false;

            var modKey1 = values[0] as ModKey?; // The ModKey for the current MenuItem
            var modKey2 = values[1] as ModKey?; // The VM_ModsMenuMugshot.CurrentSourcePlugin

            if (modKey1.HasValue && modKey2.HasValue)
            {
                return modKey1.Value.Equals(modKey2.Value);
            }
            
            // If one is null and the other isn't, they are not equal.
            // If both are null (no value), consider them not equal for checkmark purposes unless specifically desired.
            return false; 
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            // This converter is used for a one-way check (source -> IsChecked).
            // ConvertBack is not expected to be called or meaningful here.
            // Returning an array of UnsetValue or DoNothing is conventional.
            return targetTypes.Select(t => DependencyProperty.UnsetValue).ToArray();
            // Or, more simply for this case where it's unlikely to be robustly used:
            // return new object[] { Binding.DoNothing, Binding.DoNothing }; 
            // Or even:
            // throw new NotSupportedException("ConvertBack is not supported for AreModKeysEqualMultiConverter.");
            // However, returning UnsetValue is generally safer to prevent binding errors if the system *does* try.
        }
    }
}