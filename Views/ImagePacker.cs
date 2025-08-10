// [ImagePacker.cs] - Refactored for stateless calculation and robust rendering
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using NPC_Plugin_Chooser_2.View_Models;
using System.Diagnostics;

namespace NPC_Plugin_Chooser_2.Views
{
    public class ImagePacker
    {
        public static double FitOriginalImagesToContainer(
            ObservableCollection<IHasMugshotImage> imagesToPackCollection,
            double availableHeight, double availableWidth,
            int xamlItemUniformMargin,
            bool normalizeAndCropImages = true)
        {
            var visibleImages = imagesToPackCollection
                .Where(x => x.IsVisible && x.OriginalDipWidth > 0 && x.OriginalDipHeight > 0 && !string.IsNullOrEmpty(x.ImagePath) && File.Exists(x.ImagePath))
                .ToList();

            if (!visibleImages.Any())
            {
                foreach (var img in imagesToPackCollection) { img.ImageWidth = 0; img.ImageHeight = 0; }
                return 1.0;
            }

            double finalPackerScale;

            if (normalizeAndCropImages)
            {
                // --- REFACTORED NORMALIZATION AND PACKING LOGIC ---

                // 1. Determine the mode size in PIXELS. This is our target for all images.
                var modePixelSize = visibleImages
                    .Select(img => new Size(img.OriginalPixelWidth, img.OriginalPixelHeight))
                    .GroupBy(size => size)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                if (modePixelSize.IsEmpty && visibleImages.Any())
                {
                    modePixelSize = new Size(visibleImages.First().OriginalPixelWidth, visibleImages.First().OriginalPixelHeight);
                }
                
                Debug.WriteLine($"[ImagePacker] Normalization Mode Size determined to be: {modePixelSize.Width}x{modePixelSize.Height}");

                if (modePixelSize.IsEmpty || modePixelSize.Width == 0)
                {
                    // Fallback to original behavior if a valid mode cannot be found
                    Debug.WriteLine("[ImagePacker] Could not determine valid mode size. Packing with original dimensions.");
                    return FitWithOriginalDimensions(imagesToPackCollection, visibleImages, availableHeight, availableWidth, xamlItemUniformMargin);
                }

                // 2. Calculate a single scale factor assuming ALL images will conform to the mode size.
                // We use a standard 96 DPI for this layout calculation.
                double modeDipWidth = modePixelSize.Width;
                double modeDipHeight = modePixelSize.Height;
                var uniformBaseDimensions = Enumerable.Repeat((modeDipWidth, modeDipHeight), visibleImages.Count).ToList();
                
                finalPackerScale = CalculatePackerScale(uniformBaseDimensions, availableHeight, availableWidth, xamlItemUniformMargin);
                Debug.WriteLine($"[ImagePacker] Calculated a UNIFORM packer scale of: {finalPackerScale:F4}");

                // 3. Apply normalization and the final uniform size in a single pass.
                foreach (var img in visibleImages)
                {
                    // A) If this specific image needs to be physically changed, do it now.
                    if (img.OriginalPixelWidth != modePixelSize.Width || img.OriginalPixelHeight != modePixelSize.Height)
                    {
                        try
                        {
                            using Image originalImage = Image.FromFile(img.ImagePath);
                            using Bitmap normalizedBitmap = CenterCropAndResize(originalImage, modePixelSize.Width, modePixelSize.Height);
                            img.MugshotSource = BitmapToImageSource(normalizedBitmap);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ImagePacker] ERROR normalizing image {img.ImagePath}: {ex.Message}");
                        }
                    }

                    // B) Apply the final calculated size to EVERY visible image.
                    // This ensures all borders are the same size, regardless of original dimensions.
                    img.ImageWidth = modeDipWidth * finalPackerScale;
                    img.ImageHeight = modeDipHeight * finalPackerScale;
                }
            }
            else
            {
                // If not normalizing, use the original logic.
                finalPackerScale = FitWithOriginalDimensions(imagesToPackCollection, visibleImages, availableHeight, availableWidth, xamlItemUniformMargin);
            }

            return finalPackerScale;
        }

        private static double FitWithOriginalDimensions(
            ObservableCollection<IHasMugshotImage> fullCollection,
            List<IHasMugshotImage> visibleImages,
            double availableHeight, double availableWidth, int xamlItemUniformMargin)
        {
            var originalDimensions = visibleImages.Select(img => (img.OriginalDipWidth, img.OriginalDipHeight)).ToList();
            double packerScale = CalculatePackerScale(originalDimensions, availableHeight, availableWidth, xamlItemUniformMargin);

            foreach (var img in fullCollection)
            {
                if (img.IsVisible)
                {
                    img.ImageWidth = img.OriginalDipWidth * packerScale;
                    img.ImageHeight = img.OriginalDipHeight * packerScale;
                }
                else
                {
                    img.ImageWidth = 0;
                    img.ImageHeight = 0;
                }
            }
            return packerScale;
        }

        private static double CalculatePackerScale(List<(double Width, double Height)> dimensions, double availableHeight, double availableWidth, int xamlItemUniformMargin)
        {
            double low = 0;
            double high = 10.0;
            if (dimensions.Any())
            {
                var firstDim = dimensions.First();
                double effectiveW = firstDim.Width + 2 * xamlItemUniformMargin;
                double effectiveH = firstDim.Height + 2 * xamlItemUniformMargin;
                if (effectiveW > 0.001) high = Math.Min(high, availableWidth / effectiveW); else high = 0.001;
                if (effectiveH > 0.001) high = Math.Min(high, availableHeight / effectiveH); else high = Math.Min(high, 0.001);
                high = Math.Max(0.001, high);
            }

            int iterations = 0;
            const int maxIterations = 100;
            const double epsilon = 0.001;

            while (high - low > epsilon && iterations < maxIterations)
            {
                double mid = low + (high - low) / 2;
                if (mid <= 0) { low = 0; break; }
                if (CanPackAll(dimensions, mid, availableWidth, availableHeight, xamlItemUniformMargin))
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
                iterations++;
            }
            return Math.Max(0.0, low);
        }

        private static BitmapSource BitmapToImageSource(Bitmap bitmap)
        {
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            memory.Position = 0;
            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.StreamSource = memory;
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.EndInit();
            bitmapImage.Freeze();
            return bitmapImage;
        }

        private static Bitmap CenterCropAndResize(Image original, int targetWidth, int targetHeight)
        {
            var result = new Bitmap(targetWidth, targetHeight);
            result.SetResolution(original.HorizontalResolution, original.VerticalResolution);
            using var g = Graphics.FromImage(result);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float srcAspect = (float)original.Width / original.Height;
            float dstAspect = (float)targetWidth / targetHeight;
            RectangleF srcRect;

            if (srcAspect > dstAspect)
            {
                float newWidth = original.Height * dstAspect;
                float xOffset = (original.Width - newWidth) / 2f;
                srcRect = new RectangleF(xOffset, 0, newWidth, original.Height);
            }
            else
            {
                float newHeight = original.Width / dstAspect;
                float yOffset = (original.Height - newHeight) / 2f;
                srcRect = new RectangleF(0, yOffset, original.Width, newHeight);
            }
            var dstRect = new RectangleF(0, 0, targetWidth, targetHeight);
            g.DrawImage(original, dstRect, srcRect, GraphicsUnit.Pixel);
            return result;
        }

        private static bool CanPackAll(List<(double Width, double Height)> baseContentSizes, double packerScale, double containerWidth, double containerHeight, int xamlItemUniformMargin)
        {
            double currentX = 0, currentY = 0, rowMaxHeight = 0;
            foreach (var (w, h) in baseContentSizes)
            {
                double itemW = (w * packerScale) + (2 * xamlItemUniformMargin);
                double itemH = (h * packerScale) + (2 * xamlItemUniformMargin);
                if (itemW > containerWidth || itemH > containerHeight) return false;
                if (currentX + itemW > containerWidth)
                {
                    currentX = 0;
                    currentY += rowMaxHeight;
                    rowMaxHeight = 0;
                }
                if (currentY + itemH > containerHeight) return false;
                currentX += itemW;
                rowMaxHeight = Math.Max(rowMaxHeight, itemH);
            }
            return true;
        }

        public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return (0, 0, 0, 0);
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                return (img.Width, img.Height, img.Width, img.Height); // Assuming 96 DPI for simplicity
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in GetImageDimensions for {imagePath}: {ex.Message}");
                return (0, 0, 0, 0);
            }
        }
    }
}
