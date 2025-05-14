// [ImagePacker.cs] - Corrected for XAML Margins
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
        /// <summary>
        /// Calculates an optimal scale factor to fit the original DIP dimensions of all visible images
        /// into the available container space, considering the XAML margin applied to each item.
        /// It then applies this scale to set the ImageWidth/ImageHeight (content size)
        /// of all images in the provided collection.
        /// </summary>
        /// <param name="imagesToPackCollection">The master collection of images. ImageWidth/Height will be set.</param>
        /// <param name="availableHeight">Container height.</param>
        /// <param name="availableWidth">Container width.</param>
        /// <param name="xamlItemUniformMargin">The uniform margin (e.g., from Margin="5") applied in XAML around each item's content.</param>
        /// <returns>The calculated scale factor that was applied to the original DIP dimensions of the content.</returns>
        public static double FitOriginalImagesToContainer(
            ObservableCollection<IHasMugshotImage> imagesToPackCollection,
            double availableHeight, double availableWidth,
            int xamlItemUniformMargin) // Renamed and simplified margin parameter
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

            // Base dimensions for packing are always the original DIP dimensions of the *content*
            var baseContentDimensions = visibleImages
                .Select(img => (img.OriginalDipWidth, img.OriginalDipHeight)) 
                .ToList();
            
            double low = 0;
            double high = 10.0; 
            if (baseContentDimensions.Any())
            {
                var firstBaseDim = baseContentDimensions.First();
                // Estimate high scale based on content fitting, actual item size will be larger due to margin
                double effectiveFirstItemW = firstBaseDim.Item1 + 2 * xamlItemUniformMargin;
                double effectiveFirstItemH = firstBaseDim.Item2 + 2 * xamlItemUniformMargin;

                if (effectiveFirstItemW > 0.001) high = Math.Min(high, availableWidth / effectiveFirstItemW); else high = 0.001;
                if (effectiveFirstItemH > 0.001) high = Math.Min(high, availableHeight / effectiveFirstItemH); else high = Math.Min(high, 0.001);
                high = Math.Max(0.001, high); 
            }

            int iterations = 0;
            const int maxIterations = 100;
            const double epsilonForBinarySearch = 0.001; 

            while (high - low > epsilonForBinarySearch && iterations < maxIterations)
            {
                double midPackerScale = low + (high - low) / 2;
                if (midPackerScale <= 0) { low = 0; break; }

                // Pass baseContentDimensions and xamlItemUniformMargin to CanPackAll
                if (CanPackAll(baseContentDimensions, midPackerScale, availableWidth, availableHeight, xamlItemUniformMargin))
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
                    // These are the dimensions for the content area (e.g., the Border's Width/Height in XAML)
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

        /// <summary>
        /// Simulates packing items into a container.
        /// </summary>
        /// <param name="baseContentSizes">List of (OriginalDipContentWidth, OriginalDipContentHeight) for each item.</param>
        /// <param name="packerScale">The scale to apply to baseContentSizes.</param>
        /// <param name="containerWidth">Total available width in the container for items (including their margins).</param>
        /// <param name="containerHeight">Total available height in the container for items (including their margins).</param>
        /// <param name="xamlItemUniformMargin">The uniform margin applied around each item's content in XAML (e.g., 5 for Margin="5").</param>
        /// <returns>True if all items can be packed, false otherwise.</returns>
        private static bool CanPackAll(
            List<(double Width, double Height)> baseContentSizes, 
            double packerScale,
            double containerWidth, double containerHeight,
            int xamlItemUniformMargin) 
        {
            const double epsilon = 0.00001; 

            double currentX = 0; // Start at the very left of the container.
            double currentY = 0; // Start at the very top of the container.
            double currentRowMaxEffectiveHeight = 0; // Tracks the max height of items *including their vertical margins* in the current row.

            if (containerWidth < 2 * xamlItemUniformMargin || containerHeight < 2 * xamlItemUniformMargin) // Basic check if container can even hold one margin thickness
            {
                 return !baseContentSizes.Any(); 
            }

            foreach (var (baseContentW, baseContentH) in baseContentSizes) 
            {
                double scaledContentW = baseContentW * packerScale;
                double scaledContentH = baseContentH * packerScale;

                // Effective size of the item including its XAML margins
                double effectiveItemW = scaledContentW + 2 * xamlItemUniformMargin;
                double effectiveItemH = scaledContentH + 2 * xamlItemUniformMargin;

                // If content is tiny, effective size is just margins. Useful to avoid div by zero or extreme scales.
                if (scaledContentW < 0.1) effectiveItemW = 2 * xamlItemUniformMargin;
                if (scaledContentH < 0.1) effectiveItemH = 2 * xamlItemUniformMargin;


                // Check if a single item (with its margins) is too big for the container
                if (effectiveItemW > containerWidth + epsilon || effectiveItemH > containerHeight + epsilon) 
                {
                     return false; 
                }

                // Try to place in current row
                if (currentX + effectiveItemW > containerWidth + epsilon) 
                {
                    // Move to next row
                    currentX = 0; // Reset X to the left edge of the container
                    currentY += currentRowMaxEffectiveHeight; // Advance Y by the height of the previous row (which already included margins)
                    currentRowMaxEffectiveHeight = 0; // Reset max height for the new row
                }

                // Check vertical fit for this new position
                if (currentY + effectiveItemH > containerHeight + epsilon) 
                {
                    return false; // Doesn't fit vertically
                }
                
                // Place item conceptually
                currentX += effectiveItemW; // Advance X by the effective width of the placed item
                currentRowMaxEffectiveHeight = Math.Max(currentRowMaxEffectiveHeight, effectiveItemH); // Update max height for current row
            }
            return true; // All items packed successfully
        }

        // GetImageDimensions remains the same
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