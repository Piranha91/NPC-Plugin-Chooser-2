using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views // Make sure this namespace matches your project's Views folder
{
    public class OrbitalRadiusConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Default transform if data is not available yet
            var defaultTransform = new TransformGroup();
            defaultTransform.Children.Add(new TranslateTransform { Y = 0 });
            defaultTransform.Children.Add(new RotateTransform { Angle = 0 });

            if (values == null || values.Length < 3 || 
                values[0] == DependencyProperty.UnsetValue || 
                values[1] == DependencyProperty.UnsetValue || 
                values[2] == DependencyProperty.UnsetValue)
            {
                return defaultTransform;
            }

            try
            {
                double width = System.Convert.ToDouble(values[0]);
                double height = System.Convert.ToDouble(values[1]);
                double modifier = System.Convert.ToDouble(values[2]);

                if (width <= 0 || height <= 0)
                {
                    return defaultTransform;
                }

                // Calculate the radius based on the smaller of the two dimensions
                double minDimension = Math.Min(width, height);
                double radius = minDimension * modifier;

                // Create the complete TransformGroup
                var transformGroup = new TransformGroup();
                // 1. Set the orbital radius by translating on the Y-axis
                transformGroup.Children.Add(new TranslateTransform { Y = -radius });
                // 2. Add the RotateTransform that the animation will target
                transformGroup.Children.Add(new RotateTransform { Angle = 0 }); 
                
                return transformGroup;
            }
            catch
            {
                // Return a safe default in case of conversion errors
                return defaultTransform;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}