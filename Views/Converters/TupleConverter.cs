using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views;

public class TupleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Clone(); // Return a copy of the values array
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}