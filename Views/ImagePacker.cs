// [ImagePacker.cs] - Modified to include placeholders in scaling
using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using System.Linq; // Ensure Linq is imported
using NPC_Plugin_Chooser_2.View_Models; // Assuming IHasMugshotImage is here

namespace NPC_Plugin_Chooser_2.Views
{
    public class ImagePacker
    {
        public static void MaximizeStartingImageSizes(
            ObservableCollection<IHasMugshotImage> appearanceMods,
            double availableHeight, double availableWidth,
            int verticalMargin, int horizontalMargin)
        {
            // --- MODIFICATION START ---
            // Consider all mods that have an image PATH and dimensions, not just HasMugshot == true
            // This means they have something to display, whether real or placeholder.
            var allModsWithDisplayableImages = appearanceMods
                .Where(x => !string.IsNullOrEmpty(x.ImagePath) && x.ImageWidth > 0 && x.ImageHeight > 0)
                .ToList();

            if (!allModsWithDisplayableImages.Any())
                return; // Nothing to pack

            // Store each mod’s original dimensions in a dictionary.
            // Use the same filtered list.
            var originalSizes = allModsWithDisplayableImages
                .ToDictionary(mod => mod, mod => (mod.ImageWidth, mod.ImageHeight));

            // For the purpose of computing the scaling factor,
            // consider only the VISIBLE mods from the displayable list.
            var visibleMods = allModsWithDisplayableImages
                .Where(x => x.IsVisible)
                .ToList();
            // --- MODIFICATION END ---


            // In case none are visible, we might choose to return or handle it differently.
            if (!visibleMods.Any())
            {
                // If no mods are visible, we don't have a basis for scaling.
                // We could potentially reset all *displayable* mods to a default size,
                // but for now, just returning leaves them at their initial calculated size.
                 return;
            }

            // Use the original sizes derived from the visible subset for calculation
            var visibleSizes = visibleMods
                .Select(vm => originalSizes[vm]) // Get dimensions from the dictionary
                .ToList();

            // Set up initial binary search bounds based on the first visible mod.
            var firstVisible = visibleMods.First();
            var firstOriginal = originalSizes[firstVisible];

            // Initial high bound estimation (can be refined)
            double high = double.MaxValue;
            if (firstOriginal.Item1 > 0) high = Math.Min(high, availableWidth / firstOriginal.Item1);
            if (firstOriginal.Item2 > 0) high = Math.Min(high, availableHeight / firstOriginal.Item2);
            high = Math.Max(0.01, high); // Ensure a small positive value

            double low = 0;

            // Optional: Tighten the upper bound further based on all visible mods (might be slightly redundant with CanPackAll but can help)
            foreach (var (w, h) in visibleSizes)
            {
                 if (w > 0) high = Math.Min(high, availableWidth / w);
                 if (h > 0) high = Math.Min(high, availableHeight / h);
            }
            high = Math.Max(low, high); // Ensure high >= low


            // Binary search to find the optimal scale factor.
            const double epsilon = 0.01;
            int iterations = 0;
            const int maxIterations = 100; // Safety break

            while (high - low > epsilon && iterations < maxIterations)
            {
                double mid = low + (high - low) / 2; // Avoid overflow with large high/low
                if (mid <= 0) break; // Scale must be positive

                if (CanPackAll(visibleSizes, mid, availableWidth, availableHeight, horizontalMargin, verticalMargin))
                {
                    // This scale `mid` works, try larger
                    low = mid;
                }
                else
                {
                    // This scale `mid` is too large
                    high = mid;
                }
                iterations++;
            }
             if (iterations >= maxIterations) System.Diagnostics.Debug.WriteLine($"Warning: ImagePacker binary search hit max iterations ({maxIterations}).");


            // --- MODIFICATION START ---
            // Apply the computed scale factor (low) to ALL mods that have displayable images
            // (including the placeholder if it met the initial criteria).
            foreach (var mod in allModsWithDisplayableImages)
            {
                if (originalSizes.TryGetValue(mod, out var original))
                {
                     // Ensure scale factor 'low' is non-negative
                     double effectiveScale = Math.Max(0.0, low);
                     mod.ImageWidth = original.Item1 * effectiveScale;
                     mod.ImageHeight = original.Item2 * effectiveScale;
                }
                else
                {
                    // This shouldn't happen if the list wasn't modified elsewhere
                    System.Diagnostics.Debug.WriteLine($"Error: Mod {mod.ImagePath} not found in originalSizes during scaling application.");
                }
            }
            // --- MODIFICATION END ---
        }

        // CanPackAll remains the same
        private static bool CanPackAll(
            List<(double ImageWidth, double ImageHeight)> sizes, // These are ORIGINAL dimensions
            double scale,
            double containerWidth,
            double containerHeight,
            int horizontalMargin,
            int verticalMargin)
        {
            double currentX = horizontalMargin; // Start with left margin
            double currentY = verticalMargin; // Start with top margin
            double currentRowHeight = 0;

            // Basic check if container is usable at all
             if (containerWidth <= horizontalMargin * 2 || containerHeight <= verticalMargin * 2)
             {
                 return !sizes.Any(); // Only possible if no items need packing
             }

            foreach (var (originalW, originalH) in sizes)
            {
                // Calculate scaled dimensions for this item
                double scaledW = originalW * scale;
                double scaledH = originalH * scale;

                 // Check if item *itself* is too big for the container at this scale
                 // Account for minimal margins needed around a single item
                 if (scaledW + horizontalMargin * 2 > containerWidth || scaledH + verticalMargin * 2 > containerHeight)
                 {
                      return false;
                 }

                // Check if adding this item horizontally overflows the current row
                // Need space for the item width AND its right margin within the container width limit
                if (currentX + scaledW + horizontalMargin > containerWidth)
                {
                    // Move to the next row
                    currentY += currentRowHeight + verticalMargin; // Add space below the previous row
                    currentX = horizontalMargin;                  // Reset X to left margin
                    currentRowHeight = 0;                         // Reset row height for the new row
                }

                // Check if adding this item vertically overflows the container height
                // Need space for the item height AND the bottom margin (implicitly checked by the start of the next row or end of loop)
                 if (currentY + scaledH + verticalMargin > containerHeight)
                 {
                     // This item would start or extend below the allowed container height
                     return false;
                 }

                // Place the item conceptually
                currentX += scaledW + horizontalMargin; // Move X position past the item and its right margin
                currentRowHeight = Math.Max(currentRowHeight, scaledH); // Update the maximum height encountered in this row
            }

            // If we successfully iterated through all items without returning false, the scale is valid
            return true;
        }


        // GetImageDimensionsInDIPs remains the same - ensure it's robust
        public static (double WidthInDIPs, double HeightInDIPs) GetImageDimensionsInDIPs(string imagePath)
        {
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                // Ensure validation is off if files might be slightly non-conformant
                using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

                // Use 96.0 as the reference DPI for WPF DIPs
                // Handle potential 0 DPI values in metadata
                double horizontalResolution = img.HorizontalResolution > 1 ? img.HorizontalResolution : 96.0;
                double verticalResolution = img.VerticalResolution > 1 ? img.VerticalResolution : 96.0;

                double widthInDIPs = img.Width * (96.0 / horizontalResolution);
                double heightInDIPs = img.Height * (96.0 / verticalResolution);

                return (widthInDIPs, heightInDIPs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetImageDimensionsInDIPs for {imagePath}: {ex.Message}");
                return (0, 0); // Return 0 dimensions on error to prevent inclusion in packing
            }
        }
    }
}