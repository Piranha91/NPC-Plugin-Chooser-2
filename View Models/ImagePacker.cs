using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

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
                        // Use ImageSharp for all processing
                        using var image = LoadImageFromSource(img);
                        
                        if (image == null) continue;

                        // CenterCropAndResize is now an ImageSharp operation
                        image.Mutate(x => x.Resize(new ResizeOptions
                        {
                            Mode = ResizeMode.Crop,
                            Size = new Size(modePixelSize.Width, modePixelSize.Height)
                        }));

                        // Convert the processed ImageSharp image back to a WPF BitmapSource
                        img.MugshotSource = ImageToImageSource(image);
                        img.OriginalPixelWidth = modePixelSize.Width;
                        img.OriginalPixelHeight = modePixelSize.Height;
                        
                        // With ImageSharp, DPI is not a primary concern. We assume screen DPI (96)
                        // where 1 pixel = 1 DIP (Device Independent Pixel).
                        img.OriginalDipWidth = modePixelSize.Width;
                        img.OriginalDipHeight = modePixelSize.Height;
                        img.OriginalDipDiagonal = Math.Sqrt(img.OriginalDipWidth * img.OriginalDipWidth +
                                                            img.OriginalDipHeight * img.OriginalDipHeight);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ImagePacker] ERROR normalizing image {img.ImagePath} with ImageSharp: {ex.Message}");
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

    // --- NEW HELPER: Loads an image into ImageSharp from either a file or an existing WPF BitmapSource ---
    private Image? LoadImageFromSource(IHasMugshotImage img)
    {
        if (img.MugshotSource is BitmapSource bmpSource)
        {
            using var memoryStream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            encoder.Save(memoryStream);
            memoryStream.Position = 0;
            return Image.Load(memoryStream);
        }
        
        return File.Exists(img.ImagePath) ? Image.Load(img.ImagePath) : null;
    }
    
    // --- REFACTORED: Converts an ImageSharp Image to a WPF BitmapSource ---
    private BitmapSource ImageToImageSource(Image image)
    {
        using var memory = new MemoryStream();
        // Save the image to the stream in PNG format
        image.Save(memory, new PngEncoder());
        memory.Position = 0;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.StreamSource = memory;
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.EndInit();
        bitmapImage.Freeze(); // Crucial for multi-threading in WPF
        return bitmapImage;
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

    // --- REFACTORED: Uses Image.Identify for better performance ---
    public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) GetImageDimensions(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath) || new FileInfo(imagePath).Length == 0)
        {
            return (0, 0, 0, 0);
        }
            
        try
        {
            // Image.Identify is extremely fast as it only reads metadata, not pixel data.
            var info = Image.Identify(imagePath);
            
            int pixelWidth = info.Width;
            int pixelHeight = info.Height;
            
            // In a modern UI context like WPF, we can treat 1 pixel as 1 DIP.
            double dipWidth = pixelWidth;
            double dipHeight = pixelHeight;
            
            return (pixelWidth, pixelHeight, dipWidth, dipHeight);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in GetImageDimensions (ImageSharp) for {imagePath}: {ex.Message}");
            return (0, 0, 0, 0);
        }
    }
}

