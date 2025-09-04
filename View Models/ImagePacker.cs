using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace NPC_Plugin_Chooser_2.View_Models;

// --- MODIFICATION: New record to carry the results of a packing operation ---
public record PackingResult(double DefinitiveWidth, double DefinitiveHeight);

public class ImagePacker
{
    // --- MODIFICATION: Event to notify subscribers that packing is complete ---
    public event Action<PackingResult> PackingCompleted;
    
    public double FitOriginalImagesToContainer(
        ObservableCollection<IHasMugshotImage> imagesToPackCollection,
        double availableHeight, double availableWidth,
        int xamlItemUniformMargin,
        bool normalizeAndCropImages = true,
        int maxMugshotsToFit = 50,
        CancellationToken token = default)
    {
        var visibleImages = imagesToPackCollection
            .Where(x => x.IsVisible && x.OriginalDipWidth > 0 && x.OriginalDipHeight > 0 &&
                        !string.IsNullOrEmpty(x.ImagePath) && File.Exists(x.ImagePath))
            .ToList();

        if (!visibleImages.Any())
        {
            foreach (var img in imagesToPackCollection)
            {
                img.ImageWidth = 0;
                img.ImageHeight = 0;
            }

            // --- MODIFICATION: Fire event even on empty set to unblock waiters ---
            PackingCompleted?.Invoke(new PackingResult(0, 0));
            return 1.0;
        }

        double finalPackerScale;

        if (normalizeAndCropImages)
        {
            var modePixelSize = visibleImages
                .Select(img => new Size(img.OriginalPixelWidth, img.OriginalPixelHeight))
                .GroupBy(size => size)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            if (modePixelSize.IsEmpty && visibleImages.Any())
            {
                modePixelSize = new Size(visibleImages.First().OriginalPixelWidth,
                    visibleImages.First().OriginalPixelHeight);
            }

            if (modePixelSize.IsEmpty || modePixelSize.Width == 0)
            {
                return FitWithOriginalDimensions(imagesToPackCollection, visibleImages, availableHeight, availableWidth,
                    xamlItemUniformMargin, maxMugshotsToFit, token);
            }

            double modeDipWidth = modePixelSize.Width;
            double modeDipHeight = modePixelSize.Height;
            var uniformBaseDimensions = Enumerable
                .Repeat((modeDipWidth, modeDipHeight), Math.Min(visibleImages.Count, maxMugshotsToFit)).ToList();

            finalPackerScale = CalculatePackerScale(uniformBaseDimensions, availableHeight, availableWidth,
                xamlItemUniformMargin, token);

            foreach (var img in visibleImages)
            {
                token.ThrowIfCancellationRequested();

                if (img.OriginalPixelWidth != modePixelSize.Width || img.OriginalPixelHeight != modePixelSize.Height)
                {
                    try
                    {
                        Image? originalImage = null;
                        MemoryStream? memoryStream = null;

                        // **FIX:** Use the in-memory BitmapSource if it exists, otherwise load from file.
                        if (img.MugshotSource is BitmapSource bmpSource)
                        {
                            memoryStream = new MemoryStream();
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                            encoder.Save(memoryStream);
                            memoryStream.Position = 0;
                            originalImage = Image.FromStream(memoryStream);
                        }
                        else
                        {
                            originalImage = Image.FromFile(img.ImagePath);
                        }

                        using (originalImage)
                        using (memoryStream) // Ensures the memory stream is also disposed
                        {
                            using Bitmap normalizedBitmap = CenterCropAndResize(originalImage, modePixelSize.Width,
                                modePixelSize.Height);

                            img.MugshotSource = BitmapToImageSource(normalizedBitmap);
                            img.OriginalPixelWidth = modePixelSize.Width;
                            img.OriginalPixelHeight = modePixelSize.Height;

                            double hRes = originalImage.HorizontalResolution > 1
                                ? originalImage.HorizontalResolution
                                : 96.0;
                            double vRes = originalImage.VerticalResolution > 1
                                ? originalImage.VerticalResolution
                                : 96.0;
                            img.OriginalDipWidth = modePixelSize.Width * (96.0 / hRes);
                            img.OriginalDipHeight = modePixelSize.Height * (96.0 / vRes);
                            img.OriginalDipDiagonal = Math.Sqrt(img.OriginalDipWidth * img.OriginalDipWidth +
                                                                img.OriginalDipHeight * img.OriginalDipHeight);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ImagePacker] ERROR normalizing image {img.ImagePath}: {ex.Message}");
                    }
                }

                img.ImageWidth = modeDipWidth * finalPackerScale;
                img.ImageHeight = modeDipHeight * finalPackerScale;
            }
        }
        else
        {
            finalPackerScale = FitWithOriginalDimensions(imagesToPackCollection, visibleImages, availableHeight,
                availableWidth, xamlItemUniformMargin, maxMugshotsToFit, token);
        }

        // --- MODIFICATION: Capture final size and fire completion event ---
        double finalWidth = 0;
        double finalHeight = 0;
        if (visibleImages.Any())
        {
            // After packing, the first visible image holds the correct final dimensions.
            finalWidth = visibleImages[0].ImageWidth;
            finalHeight = visibleImages[0].ImageHeight;
        }
        PackingCompleted?.Invoke(new PackingResult(finalWidth, finalHeight));
        // --- END MODIFICATION ---

        return finalPackerScale;
    }

    private double FitWithOriginalDimensions(
        ObservableCollection<IHasMugshotImage> fullCollection,
        List<IHasMugshotImage> visibleImages,
        double availableHeight, double availableWidth, int xamlItemUniformMargin,
        int maxMugshotsToFit, CancellationToken token = default)
    {
        var dimensionsToPack = visibleImages.Take(maxMugshotsToFit)
            .Select(img => (img.OriginalDipWidth, img.OriginalDipHeight)).ToList();
        double packerScale =
            CalculatePackerScale(dimensionsToPack, availableHeight, availableWidth, xamlItemUniformMargin, token);

        foreach (var img in fullCollection)
        {
            token.ThrowIfCancellationRequested();
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

    private double CalculatePackerScale(List<(double Width, double Height)> dimensions, double availableHeight,
        double availableWidth, int xamlItemUniformMargin, CancellationToken token = default)
    {
        double low = 0;
        double high = 10.0;
        if (dimensions.Any())
        {
            var firstDim = dimensions.First();
            double effectiveW = firstDim.Width + 2 * xamlItemUniformMargin;
            double effectiveH = firstDim.Height + 2 * xamlItemUniformMargin;
            if (effectiveW > 0.001) high = Math.Min(high, availableWidth / effectiveW);
            else high = 0.001;
            if (effectiveH > 0.001) high = Math.Min(high, availableHeight / effectiveH);
            else high = Math.Min(high, 0.001);
            high = Math.Max(0.001, high);
        }

        int iterations = 0;
        const int maxIterations = 100;
        const double epsilon = 0.001;

        while (high - low > epsilon && iterations < maxIterations)
        {
            token.ThrowIfCancellationRequested();
            double mid = low + (high - low) / 2;
            if (mid <= 0)
            {
                low = 0;
                break;
            }

            if (CanPackAll(dimensions, mid, availableWidth, availableHeight, xamlItemUniformMargin, token))
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

    private BitmapSource BitmapToImageSource(Bitmap bitmap)
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

    private Bitmap CenterCropAndResize(Image original, int targetWidth, int targetHeight)
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

    private bool CanPackAll(List<(double Width, double Height)> baseContentSizes, double packerScale,
        double containerWidth, double containerHeight, int xamlItemUniformMargin, CancellationToken token = default)
    {
        double currentX = 0, currentY = 0, rowMaxHeight = 0;
        foreach (var (w, h) in baseContentSizes)
        {
            token.ThrowIfCancellationRequested();
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

    public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(
        string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return (0, 0, 0, 0);
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
            Debug.WriteLine($"Error in GetImageDimensions for {imagePath}: {ex.Message}");
            return (0, 0, 0, 0);
        }
    }
}

