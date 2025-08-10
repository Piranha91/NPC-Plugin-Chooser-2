// [ImagePacker.cs] - Corrected with robust drawing logic
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging; // Required for BitmapSource
using NPC_Plugin_Chooser_2.View_Models;
using System.Diagnostics; // Required for Debug.WriteLine

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
                foreach (var img in imagesToPackCollection)
                {
                    img.ImageWidth = 0;
                    img.ImageHeight = 0;
                }
                return 1.0;
            }

            if (normalizeAndCropImages)
            {
                var modeSize = visibleImages
                    .Select(img => new Size(img.OriginalPixelWidth, img.OriginalPixelHeight))
                    .GroupBy(size => size)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                if (modeSize.IsEmpty && visibleImages.Any())
                {
                    modeSize = new Size(visibleImages.First().OriginalPixelWidth, visibleImages.First().OriginalPixelHeight);
                }

                Debug.WriteLine($"[ImagePacker] Normalization Mode Size determined to be: {modeSize.Width}x{modeSize.Height}");

                if (modeSize.IsEmpty || modeSize.Width == 0 || modeSize.Height == 0)
                {
                    Debug.WriteLine("[ImagePacker] Warning: Mode size is invalid. Skipping normalization.");
                }
                else
                {
                    foreach (var img in visibleImages)
                    {
                        if (img.OriginalPixelWidth != modeSize.Width || img.OriginalPixelHeight != modeSize.Height)
                        {
                            Debug.WriteLine($"[ImagePacker] Normalizing image: {Path.GetFileName(img.ImagePath)} (Original: {img.OriginalPixelWidth}x{img.OriginalPixelHeight})");
                            try
                            {
                                using Image originalImage = Image.FromFile(img.ImagePath);
                                using Bitmap normalizedBitmap = CenterCropAndResize(originalImage, modeSize.Width, modeSize.Height);
                                BitmapSource normalizedSource = BitmapToImageSource(normalizedBitmap);

                                img.MugshotSource = normalizedSource;
                                img.OriginalPixelWidth = modeSize.Width;
                                img.OriginalPixelHeight = modeSize.Height;

                                double hRes = originalImage.HorizontalResolution > 1 ? originalImage.HorizontalResolution : 96.0;
                                double vRes = originalImage.VerticalResolution > 1 ? originalImage.VerticalResolution : 96.0;
                                img.OriginalDipWidth = modeSize.Width * (96.0 / hRes);
                                img.OriginalDipHeight = modeSize.Height * (96.0 / vRes);
                                img.OriginalDipDiagonal = Math.Sqrt(img.OriginalDipWidth * img.OriginalDipWidth + img.OriginalDipHeight * img.OriginalDipHeight);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[ImagePacker] ERROR normalizing image {img.ImagePath} in memory: {ex.Message}");
                            }
                        }
                    }
                }
            }

            var baseContentDimensions = visibleImages
                .Select(img => (img.OriginalDipWidth, img.OriginalDipHeight))
                .ToList();

            double low = 0;
            double high = 10.0;
            if (baseContentDimensions.Any())
            {
                var firstBaseDim = baseContentDimensions.First();
                double effectiveFirstItemW = firstBaseDim.Item1 + 2 * xamlItemUniformMargin;
                double effectiveFirstItemH = firstBaseDim.Item2 + 2 * xamlItemUniformMargin;

                if (effectiveFirstItemW > 0.001) high = Math.Min(high, availableWidth / effectiveFirstItemW); else high = 0.001;
                if (effectiveFirstItemH > 0.001) high = Math.Min(high, availableHeight / effectiveFirstItemH); else high = 0.001;
                high = Math.Max(0.001, high);
            }

            int iterations = 0;
            const int maxIterations = 100;
            const double epsilonForBinarySearch = 0.001;

            while (high - low > epsilonForBinarySearch && iterations < maxIterations)
            {
                double midPackerScale = low + (high - low) / 2;
                if (midPackerScale <= 0) { low = 0; break; }

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
                if (img.IsVisible && img.OriginalDipWidth > 0 && img.OriginalDipHeight > 0)
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

        private static BitmapSource BitmapToImageSource(System.Drawing.Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
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
        }

        private static Bitmap CenterCropAndResize(Image original, int targetWidth, int targetHeight)
        {
            Debug.WriteLine($"    -> CenterCropAndResize: Original={original.Width}x{original.Height}, Target={targetWidth}x{targetHeight}");
            var result = new Bitmap(targetWidth, targetHeight);
            result.SetResolution(original.HorizontalResolution, original.VerticalResolution);

            using var g = Graphics.FromImage(result);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // --- REVISED AND CORRECTED DRAWING LOGIC ---
            float srcAspect = (float)original.Width / original.Height;
            float dstAspect = (float)targetWidth / targetHeight;
            
            RectangleF srcRect;

            // Determine the portion of the source image to use to match the destination aspect ratio
            if (srcAspect > dstAspect) // Source is wider than destination -> crop horizontally
            {
                float newWidth = original.Height * dstAspect;
                float xOffset = (original.Width - newWidth) / 2f;
                srcRect = new RectangleF(xOffset, 0, newWidth, original.Height);
                Debug.WriteLine($"    -> Source is WIDER. Cropping horizontally. Source Rect to use: {srcRect}");
            }
            else // Source is taller or same aspect as destination -> crop vertically
            {
                float newHeight = original.Width / dstAspect;
                float yOffset = (original.Height - newHeight) / 2f;
                srcRect = new RectangleF(0, yOffset, original.Width, newHeight);
                Debug.WriteLine($"    -> Source is TALLER or SAME. Cropping vertically. Source Rect to use: {srcRect}");
            }

            // The destination rectangle is always the entire target bitmap area
            RectangleF dstRect = new RectangleF(0, 0, targetWidth, targetHeight);

            // This DrawImage overload handles scaling the source rectangle to fit the destination rectangle
            g.DrawImage(original, dstRect, srcRect, GraphicsUnit.Pixel);

            return result;
        }


        private static bool CanPackAll(
            List<(double Width, double Height)> baseContentSizes,
            double packerScale,
            double containerWidth, double containerHeight,
            int xamlItemUniformMargin)
        {
            const double epsilon = 0.00001;
            double currentX = 0, currentY = 0, currentRowMaxEffectiveHeight = 0;

            if (containerWidth < 2 * xamlItemUniformMargin || containerHeight < 2 * xamlItemUniformMargin)
                return !baseContentSizes.Any();

            foreach (var (baseContentW, baseContentH) in baseContentSizes)
            {
                double scaledContentW = baseContentW * packerScale;
                double scaledContentH = baseContentH * packerScale;
                double effectiveItemW = scaledContentW + 2 * xamlItemUniformMargin;
                double effectiveItemH = scaledContentH + 2 * xamlItemUniformMargin;

                if (scaledContentW < 0.1) effectiveItemW = 2 * xamlItemUniformMargin;
                if (scaledContentH < 0.1) effectiveItemH = 2 * xamlItemUniformMargin;

                if (effectiveItemW > containerWidth + epsilon || effectiveItemH > containerHeight + epsilon) return false;

                if (currentX + effectiveItemW > containerWidth + epsilon)
                {
                    currentX = 0;
                    currentY += currentRowMaxEffectiveHeight;
                    currentRowMaxEffectiveHeight = 0;
                }

                if (currentY + effectiveItemH > containerHeight + epsilon) return false;

                currentX += effectiveItemW;
                currentRowMaxEffectiveHeight = Math.Max(currentRowMaxEffectiveHeight, effectiveItemH);
            }
            return true;
        }

        public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                return (0, 0, 0, 0);
            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                int pixelWidth = img.Width;
                int pixelHeight = img.Height;
                double hRes = img.HorizontalResolution > 1 ? img.HorizontalResolution : 96.0;
                double vRes = img.VerticalResolution > 1 ? img.VerticalResolution : 96.0;
                double dipWidth = pixelWidth * (96.0 / hRes);
                double dipHeight = pixelHeight * (96.0 / vRes);
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
