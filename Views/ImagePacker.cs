using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views
{
    public class ImagePacker
    {
        public static void MaximizeStartingImageSizes(
            ObservableCollection<IHasMugshotImage> appearanceMods,
            double availableHeight, double availableWidth,
            int verticalMargin, int horizontalMargin)
        {
            // Retrieve all mods that have an image.
            var allModsWithImages = appearanceMods
                .Where(x => x.HasMugshot)
                .ToList();

            if (!allModsWithImages.Any())
                return;

            // Store each mod’s original dimensions in a dictionary.
            var originalSizes = allModsWithImages
                .ToDictionary(mod => mod, mod => (mod.ImageWidth, mod.ImageHeight));

            // For the purpose of computing the scaling factor,
            // consider only the visible mods.
            var visibleMods = allModsWithImages
                .Where(x => x.IsVisible)
                .ToList();

            // In case none are visible, you might choose to return or handle it differently.
            if (!visibleMods.Any())
                return;

            var visibleSizes = visibleMods
                .Select(vm => originalSizes[vm])
                .ToList();

            // Set up initial binary search bounds.
            var firstVisible = visibleMods.First();
            var firstOriginal = originalSizes[firstVisible];
            double low = 0;
            double high = Math.Min(
                availableWidth / firstOriginal.Item1,
                availableHeight / firstOriginal.Item2);

            // Tighten the upper bound by checking each visible mod.
            foreach (var (w, h) in visibleSizes)
            {
                double maxScaleW = availableWidth / (w + horizontalMargin);
                double maxScaleH = availableHeight / (h + verticalMargin);
                high = Math.Min(high, Math.Min(maxScaleW, maxScaleH));
            }

            // Binary search to find the optimal scale factor.
            const double epsilon = 0.01;
            while (high - low > epsilon)
            {
                double mid = (low + high) / 2;
                if (CanPackAll(visibleSizes, mid, availableWidth, availableHeight, horizontalMargin, verticalMargin))
                    low = mid;
                else
                    high = mid;
            }

            // Apply the computed scale factor (low) to all mods that have images.
            foreach (var mod in allModsWithImages)
            {
                var original = originalSizes[mod];
                mod.ImageWidth = original.Item1 * low;
                mod.ImageHeight = original.Item2 * low;
            }
        }

        private static bool CanPackAll(
            List<(double ImageWidth, double ImageHeight)> sizes,
            double scale,
            double containerWidth,
            double containerHeight,
            int horizontalMargin,
            int verticalMargin)
        {
            double x = 0, y = 0, rowHeight = 0;

            foreach (var (w, h) in sizes)
            {
                double scaledW = w * scale;
                double scaledH = h * scale;

                if (x + scaledW > containerWidth)
                {
                    // Move to next row.
                    x = 0;
                    y += rowHeight + verticalMargin;
                    rowHeight = 0;
                }

                if (scaledW > containerWidth || scaledH > containerHeight || y + scaledH > containerHeight)
                    return false;

                x += scaledW + horizontalMargin;
                rowHeight = Math.Max(rowHeight, scaledH);
            }

            return true;
        }

        public static (double WidthInDIPs, double HeightInDIPs) GetImageDimensionsInDIPs(string imagePath)
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

            double widthInDIPs = img.Width * (96.0 / img.HorizontalResolution);
            double heightInDIPs = img.Height * (96.0 / img.VerticalResolution);

            return (widthInDIPs, heightInDIPs);
        }
    }
}
