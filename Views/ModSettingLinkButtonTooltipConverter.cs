// Views/Converters/ModSettingLinkButtonTooltipConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Or NPC_Plugin_Chooser_2.Views.Converters
{
    public class ModSettingLinkButtonTooltipConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
            {
                return "Show NPCs for this mod"; // Default fallback
            }

            bool hasValidMugshots = false;
            if (values[0] is bool bVal)
            {
                hasValidMugshots = bVal;
            }

            int npcCount = 0;
            if (values[1] is int iVal)
            {
                npcCount = iVal;
            }

            if (npcCount == 0)
            {
                return "No NPCs defined for this mod";
            }

            if (hasValidMugshots)
            {
                return $"Show {npcCount} NPC mugshot{(npcCount == 1 ? "" : "s")} for this mod";
            }
            else
            {
                return $"Show placeholders for {npcCount} NPC{(npcCount == 1 ? "" : "s")} (actual mugshot folder not found or is empty)";
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}