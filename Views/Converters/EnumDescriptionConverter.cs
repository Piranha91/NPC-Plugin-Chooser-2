// EnumDescriptionConverter.cs
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Data;

namespace NPC_Plugin_Chooser_2.Views // Adjust namespace if needed
{
    public class EnumDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;

            Enum myEnum = (Enum)value;
            string description = GetEnumDescription(myEnum);
            return description;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // ConvertBack is not typically needed for display purposes
            throw new NotImplementedException();
        }

        public static string GetEnumDescription(Enum enumObj)
        {
            FieldInfo? fieldInfo = enumObj.GetType().GetField(enumObj.ToString());
            if (fieldInfo == null) return enumObj.ToString(); // Fallback

            object[] attribArray = fieldInfo.GetCustomAttributes(false);

            if (attribArray.Length == 0)
            {
                return enumObj.ToString(); // Fallback if no description
            }
            else
            {
                DescriptionAttribute? attrib = attribArray.OfType<DescriptionAttribute>().FirstOrDefault();
                return attrib?.Description ?? enumObj.ToString(); // Use description or fallback
            }
        }
    }
}