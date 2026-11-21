using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using NPC_Plugin_Chooser_2.BackEnd;
using Size = System.Drawing.Size;

namespace NPC_Plugin_Chooser_2.View_Models;

// --- MODIFICATION: New record to carry the results of a packing operation ---
public record PackingResult(double DefinitiveWidth, double DefinitiveHeight);

public class ImagePacker
{
    // --- MODIFICATION: Event to notify subscribers that packing is complete ---
    public event EventHandler<PackingCompletedEventArgs> PackingCompleted;
    
    public class PackingCompletedEventArgs : EventArgs
    {
        public PackingResult Result { get; }
        public PackingCompletedEventArgs(PackingResult result) { Result = result; }
    }
    
    public double FitOriginalImagesToContainer(
        ObservableCollection<IHasMugshotImage> imagesToPackCollection,
        double availableHeight, double availableWidth,
        int xamlItemUniformMargin,
        bool normalizeAndCropImages = true,
        int maxMugshotsToFit = 50,
        CancellationToken token = default)
    {
        var visibleImages = imagesToPackCollection
            .Where(x => x.IsVisible && x.OriginalDipWidth > 0 && x.OriginalDipHeight > 0)
            .ToList();

        if (!visibleImages.Any())
        {
            foreach (var img in imagesToPackCollection)
            {
                img.ImageWidth = 0;
                img.ImageHeight = 0;
            }

            // --- MODIFICATION: Fire event even on empty set to unblock waiters ---
            PackingCompleted?.Invoke(this, new PackingCompletedEventArgs(new PackingResult(0, 0)));
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
                        var source = LoadBitmapSource(img);

                        if (source == null) continue;

                        img.MugshotSource = CenterCropResize(source, modePixelSize.Width, modePixelSize.Height);
                        img.OriginalPixelWidth = modePixelSize.Width;
                        img.OriginalPixelHeight = modePixelSize.Height;

                        // The resized bitmap is rendered at 96 DPI, so 1 pixel = 1 DIP.
                        img.OriginalDipWidth = modePixelSize.Width;
                        img.OriginalDipHeight = modePixelSize.Height;
                        img.OriginalDipDiagonal = Math.Sqrt(img.OriginalDipWidth * img.OriginalDipWidth +
                                                            img.OriginalDipHeight * img.OriginalDipHeight);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ImagePacker] ERROR normalizing image {img.ImagePath}: {ex.Message}");
                    }
                }

                img.ImageWidth = modePixelSize.Width * finalPackerScale;
                img.ImageHeight = modePixelSize.Height * finalPackerScale;
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
        PackingCompleted?.Invoke(this, new PackingCompletedEventArgs(new PackingResult(finalWidth, finalHeight)));

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

    /// <summary>The tile's current bitmap, or its file decoded (frozen) when it has none.</summary>
    private static BitmapSource? LoadBitmapSource(IHasMugshotImage img)
    {
        if (img.MugshotSource is BitmapSource bmpSource) return bmpSource;
        if (!File.Exists(img.ImagePath)) return null;

        // Stream rather than UriSource: WPF caches UriSource bitmaps per URI, which would serve a
        // stale image after a mugshot is regenerated in place.
        using var fs = File.OpenRead(img.ImagePath);
        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = fs;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    /// <summary>
    /// Scales <paramref name="source"/> to cover <paramref name="targetWidth"/> x <paramref name="targetHeight"/>
    /// and crops the overflow equally from both sides: the center-anchored "crop" resize ImageSharp's
    /// ResizeMode.Crop performed here before. Fant resampling stays closest to ImageSharp's bicubic output
    /// (PSNR ~48 dB on a 600-mugshot sample) at ~5 ms instead of ~80 ms per image (tools/imagebench).
    /// Returns a frozen 96-DPI Pbgra32 bitmap.
    /// </summary>
    internal static BitmapSource CenterCropResize(BitmapSource source, int targetWidth, int targetHeight)
    {
        int sw = source.PixelWidth, sh = source.PixelHeight;
        double scale = Math.Max(targetWidth / (double)sw, targetHeight / (double)sh);
        int cropW = Math.Clamp((int)Math.Round(targetWidth / scale), 1, sw);
        int cropH = Math.Clamp((int)Math.Round(targetHeight / scale), 1, sh);
        var cropped = new CroppedBitmap(source, new Int32Rect((sw - cropW) / 2, (sh - cropH) / 2, cropW, cropH));
        cropped.Freeze();

        // BitmapScalingMode must sit on a DrawingGroup: set on the DrawingVisual it is ignored
        // (measured; Fant and NearestNeighbor rendered identically). The explicit DIP rect makes
        // the source's own DPI irrelevant.
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.Fant);
        group.Children.Add(new ImageDrawing(cropped, new Rect(0, 0, targetWidth, targetHeight)));
        group.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawDrawing(group);
        }

        var target = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
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

    /// <summary>Pixel dimensions read from the file header only (no pixel decode); zeros if unreadable.</summary>
    public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath) || new FileInfo(imagePath).Length == 0)
        {
            return (0, 0, 0, 0);
        }

        if (!ImageHeaderReader.TryGetDimensions(imagePath, out int pixelWidth, out int pixelHeight))
        {
            Debug.WriteLine($"GetImageDimensions: could not read image header for {imagePath}");
            return (0, 0, 0, 0);
        }

        // In a modern UI context like WPF, we can treat 1 pixel as 1 DIP.
        return (pixelWidth, pixelHeight, pixelWidth, pixelHeight);
    }
}

