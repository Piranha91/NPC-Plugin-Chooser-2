using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NPC_Plugin_Chooser_2.Views // Ensure this namespace is correct
{
    public static class DynamicRadiusBehavior
    {
        // 1. ADD THE NEW ATTACHED PROPERTY FOR THE MULTIPLIER
        public static readonly DependencyProperty RadiusMultiplierProperty =
            DependencyProperty.RegisterAttached("RadiusMultiplier", typeof(double), typeof(DynamicRadiusBehavior), new PropertyMetadata(0.75, OnDimensionsChanged));

        public static void SetRadiusMultiplier(UIElement element, double value) => element.SetValue(RadiusMultiplierProperty, value);
        public static double GetRadiusMultiplier(UIElement element) => (double)element.GetValue(RadiusMultiplierProperty);


        // Existing Attached Property for ImageWidth
        public static readonly DependencyProperty ImageWidthProperty =
            DependencyProperty.RegisterAttached("ImageWidth", typeof(double), typeof(DynamicRadiusBehavior), new PropertyMetadata(0.0, OnDimensionsChanged));

        public static void SetImageWidth(UIElement element, double value) => element.SetValue(ImageWidthProperty, value);
        public static double GetImageWidth(UIElement element) => (double)element.GetValue(ImageWidthProperty);

        // Existing Attached Property for ImageHeight
        public static readonly DependencyProperty ImageHeightProperty =
            DependencyProperty.RegisterAttached("ImageHeight", typeof(double), typeof(DynamicRadiusBehavior), new PropertyMetadata(0.0, OnDimensionsChanged));

        public static void SetImageHeight(UIElement element, double value) => element.SetValue(ImageHeightProperty, value);
        public static double GetImageHeight(UIElement element) => (double)element.GetValue(ImageHeightProperty);

        private static void OnDimensionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Image image) return;

            // Read all the required values from the attached properties
            double width = GetImageWidth(image);
            double height = GetImageHeight(image);
            double multiplier = GetRadiusMultiplier(image); // Get the value from our new property

            if (width <= 0 || height <= 0) return;

            var currentTransform = image.RenderTransform;
            
            if (currentTransform.IsFrozen)
            {
                currentTransform = currentTransform.Clone();
                image.RenderTransform = currentTransform;
            }
            
            if (currentTransform is not TransformGroup group || group.Children.Count < 2 || group.Children[0] is not TranslateTransform)
            {
                return;
            }

            // 2. UPDATE THE CALCULATION TO USE THE MULTIPLIER
            double minDimension = Math.Min(width, height);
            double radius = minDimension * multiplier;

            var translateTransform = (TranslateTransform)group.Children[0];
            
            translateTransform.Y = -radius;
        }
    }
}