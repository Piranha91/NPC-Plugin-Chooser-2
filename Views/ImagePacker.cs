using System.Collections.ObjectModel;
using System.Drawing;
using System.IO;
using NPC_Plugin_Chooser_2.View_Models;

namespace NPC_Plugin_Chooser_2.Views;

public class ImagePacker
{

    public static void MaximizeStartingImageSizes(
        ObservableCollection<VM_AppearanceMod> appearanceMods,
        double availableHeight, double availableWidth,
        int verticalMargin, int horizontalMargin)
    {
        var vmsWithImages = appearanceMods
            .Where(x => x.HasMugshot && x.IsVisible)
            .ToList(); // ✅ only consider visible mods

        if (!vmsWithImages.Any())
            return;

        var originalSizes = vmsWithImages
            .Select(vm => (vm.ImageWidth, vm.ImageHeight))
            .ToList();

        var firstImage = vmsWithImages.First();
        
        double low = 0;
        //double high = 1;
        double high = Math.Min(availableWidth / firstImage.ImageWidth, availableHeight / firstImage.ImageHeight);

        foreach (var (w, h) in originalSizes)
        {
            double maxScaleW = availableWidth / (w + horizontalMargin);
            double maxScaleH = availableHeight / (h + verticalMargin);
            high = Math.Min(high, Math.Min(maxScaleW, maxScaleH));
        }

        const double epsilon = 0.01;

        while (high - low > epsilon)
        {
            double mid = (low + high) / 2;
            if (CanPackAll(originalSizes, mid, availableWidth, availableHeight, horizontalMargin, verticalMargin))
                low = mid;
            else
                high = mid;
        }

        foreach (var (vm, original) in vmsWithImages.Zip(originalSizes))
        {
            vm.ImageWidth = original.ImageWidth * low;
            vm.ImageHeight = original.ImageHeight * low;
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
                // Move to next row
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
