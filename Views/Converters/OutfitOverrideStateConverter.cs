using System.Globalization;
using System.Windows.Data;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

public class OutfitOverrideStateConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            !(values[0] is OutfitOverride itemOverride) ||
            !(values[1] is FormKey npcFormKey) ||
            !(values[2] is VM_NpcSelectionBar vm))
        {
            return false;
        }

        return vm.GetNpcOutfitOverride(npcFormKey) == itemOverride;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}