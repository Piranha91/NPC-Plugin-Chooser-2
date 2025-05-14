// [ImagePacker.cs] - Corrected CanPackAll with epsilon
using System;
using System.Collections.Generic; 
using System.Collections.ObjectModel;
using System.Drawing; 
using System.IO;
using System.Linq; 
using NPC_Plugin_Chooser_2.View_Models; 

namespace NPC_Plugin_Chooser_2.Views
{
    public class ImagePacker
    {
        public static double FitOriginalImagesToContainer(
            ObservableCollection<IHasMugshotImage> imagesToPackCollection,
            double availableHeight, double availableWidth,
            int verticalMargin, int horizontalMargin)
        {
            var visibleImages = imagesToPackCollection
                .Where(x => x.IsVisible && x.OriginalDipWidth > 0 && x.OriginalDipHeight > 0 && !string.IsNullOrEmpty(x.ImagePath))
                .ToList();

            if (!visibleImages.Any())
            {
                foreach (var img in imagesToPackCollection)
                {
                    img.ImageWidth = 0;
                    img.ImageHeight = 0;
                }
                return 1.0; 
            }

            var baseDimensionsForPacking = visibleImages
                .Select(img => (img.OriginalDipWidth, img.OriginalDipHeight)) 
                .ToList();
            
            double low = 0;
            double high = 10.0; 
            if (baseDimensionsForPacking.Any())
            {
                var firstBaseDim = baseDimensionsForPacking.First();
                if (firstBaseDim.Item1 > 0.001) high = Math.Min(high, availableWidth / firstBaseDim.Item1); else high = 0.001;
                if (firstBaseDim.Item2 > 0.001) high = Math.Min(high, availableHeight / firstBaseDim.Item2); else high = Math.Min(high, 0.001);
                high = Math.Max(0.001, high); 
            }

            int iterations = 0;
            const int maxIterations = 100;
            const double epsilonForBinarySearch = 0.001; // Epsilon for the binary search loop itself

            while (high - low > epsilonForBinarySearch && iterations < maxIterations)
            {
                double midPackerScale = low + (high - low) / 2;
                if (midPackerScale <= 0) { low = 0; break; }

                if (CanPackAll(baseDimensionsForPacking, midPackerScale, availableWidth, availableHeight, horizontalMargin, verticalMargin))
                {
                    low = midPackerScale; 
                }
                else
                {
                    high = midPackerScale; 
                }
                iterations++;
            }
            
            double finalPackerScale = Math.Max(0.0, low); 

            foreach (var img in imagesToPackCollection)
            {
                if (img.IsVisible && img.OriginalDipWidth > 0 && img.OriginalDipHeight > 0 && !string.IsNullOrEmpty(img.ImagePath))
                {
                    img.ImageWidth = img.OriginalDipWidth * finalPackerScale;
                    img.ImageHeight = img.OriginalDipHeight * finalPackerScale;
                }
                else 
                {
                    img.ImageWidth = 0;
                    img.ImageHeight = 0;
                }
            }
            
            return finalPackerScale;
        }

        private static bool CanPackAll(
            List<(double Width, double Height)> baseSizes, 
            double packerScale,
            double containerWidth,
            double containerHeight,
            int horizontalMargin,
            int verticalMargin)
        {
            // *** DEFINE EPSILON FOR COMPARISONS WITHIN THIS METHOD ***
            const double epsilon = 0.00001; // A small value for floating point comparisons

            double currentX = horizontalMargin; 
            double currentY = verticalMargin; 
            double currentRowHeight = 0;

            if (containerWidth <= horizontalMargin * 2 || containerHeight <= verticalMargin * 2)
            {
                 return !baseSizes.Any(); 
            }

            foreach (var (baseW, baseH) in baseSizes) 
            {
                double scaledW = baseW * packerScale;
                double scaledH = baseH * packerScale;

                 if (scaledW < 0.1 || scaledH < 0.1) 
                 {
                 }
                 // Use epsilon in comparisons
                 else if (scaledW + horizontalMargin * 2 > containerWidth + epsilon || scaledH + verticalMargin * 2 > containerHeight + epsilon) 
                 {
                      return false; 
                 }

                // Use epsilon in comparisons
                if (currentX + scaledW + horizontalMargin > containerWidth + epsilon) 
                {
                    currentY += currentRowHeight + verticalMargin; 
                    currentX = horizontalMargin;                  
                    currentRowHeight = 0;                         
                }

                // Use epsilon in comparisons
                 if (currentY + scaledH + verticalMargin > containerHeight + epsilon) 
                 {
                     return false;
                 }
                
                currentX += scaledW + horizontalMargin; 
                currentRowHeight = Math.Max(currentRowHeight, scaledH); 
            }
            return true;
        }

        public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath))
                return (0,0,0,0);
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                int pixelWidth = img.Width;
                int pixelHeight = img.Height;
                double horizontalResolution = img.HorizontalResolution > 1 ? img.HorizontalResolution : 96.0;
                double verticalResolution = img.VerticalResolution > 1 ? img.VerticalResolution : 96.0;
                double dipWidth = pixelWidth * (96.0 / horizontalResolution);
                double dipHeight = pixelHeight * (96.0 / verticalResolution);
                return (pixelWidth, pixelHeight, dipWidth, dipHeight);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetImageDimensions for {imagePath}: {ex.Message}");
                return (0, 0, 0, 0); 
            }
        }
    }
}