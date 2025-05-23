// Add this converter, e.g., in Views/Converters folder
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media; // For Brushes if needed later

namespace NPC_Plugin_Chooser_2.Views
{
    public class ModStatusToolTipMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 3 ||
                !(values[0] is bool hasMugshotPath) ||
                !(values[1] is bool hasModPaths) ||
                !(values[2] is bool isMugshotOnly))
            {
                return "Status: Unknown"; // Default or error
            }

            string baseStatus;
            if (hasMugshotPath && hasModPaths) baseStatus = "Status: OK (Has Mugshot path and Mod Data path(s))";
            else if (!hasMugshotPath && hasModPaths) baseStatus = "Status: Partial (Has Mod Data path(s), but no Mugshot path assigned)";
            else if (hasMugshotPath && !hasModPaths) baseStatus = "Status: Partial (Has Mugshot path, but no Mod Data path(s) assigned)";
            else baseStatus = "Status: Incomplete (Requires Mugshot Path and/or Mod Data Path assignment)";

            if (isMugshotOnly)
            {
                return baseStatus + "\n(Note: This entry was created from a Mugshot folder and will not be saved in settings)";
            }
            else
            {
                return baseStatus;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}