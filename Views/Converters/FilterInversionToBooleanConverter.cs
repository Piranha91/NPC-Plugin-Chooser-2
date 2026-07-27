using System.Globalization;
using System.Windows.Data;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// Bridges a filter row's <see cref="FilterInversionType"/> to the "Not" checkbox that
/// precedes the row's field dropdown. Checked == <see cref="FilterInversionType.IsNot"/>,
/// so the row reads as a negated criterion ("Not | In Appearance Mod | Bijin") instead of
/// needing a copula that only fits some of the fields.
/// </summary>
public class FilterInversionToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is FilterInversionType inversion && inversion == FilterInversionType.IsNot;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? FilterInversionType.IsNot : FilterInversionType.Is;
    }
}
