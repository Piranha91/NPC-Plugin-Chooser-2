using System;
using System.Globalization;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views
{
    /// <summary>
    /// Substitutes a non-breaking space for an empty/whitespace-only string so a blank line
    /// still occupies a full line of height.
    ///
    /// Needed by the Run tab's log, which renders one TextBlock per line: a TextBlock with no
    /// text measures to zero height, which would swallow the blank separator lines the log
    /// emits (e.g. "\n--- Loading resources for batch ---"). Display-only — the underlying
    /// value is untouched, so copying a blank line still yields a blank line.
    /// </summary>
    [ValueConversion(typeof(string), typeof(string))]
    public class BlankLineToSpaceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var text = value as string;
            return string.IsNullOrWhiteSpace(text) ? " " : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
