using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Real encoded images (made with WPF's own encoders) through <see cref="ImageHeaderReader"/>,
/// including the WIC fallback for formats it does not hand-parse, and
/// <see cref="ImagePacker"/>'s center-crop normalize step, which replaced ImageSharp's
/// ResizeMode.Crop. WPF imaging runs on the shared STA fixture like the app's UI thread.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class ImageWicRoundTripTests
{
    private readonly WpfStaFixture _sta;
    public ImageWicRoundTripTests(WpfStaFixture sta) => _sta = sta;

    /// <summary>Opaque Bgra32 bitmap split into vertical thirds: red | green | blue.</summary>
    private static BitmapSource Thirds(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int third = x * 3 / width;
                px[i + 0] = (byte)(third == 2 ? 255 : 0); // B
                px[i + 1] = (byte)(third == 1 ? 255 : 0); // G
                px[i + 2] = (byte)(third == 0 ? 255 : 0); // R
                px[i + 3] = 255;
            }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, px, width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(BitmapSource s, int x, int y)
    {
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        c.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    [Fact]
    public Task EncodedImages_HeaderDimensionsAndExtension() => _sta.RunOnStaAsync(() =>
    {
        var src = Thirds(123, 45);
        var cases = new (string Ext, BitmapEncoder Encoder)[]
        {
            ("png", new PngBitmapEncoder()),
            ("jpg", new JpegBitmapEncoder()),
            ("bmp", new BmpBitmapEncoder()),   // not hand-parsed: exercises the WIC fallback
            ("tiff", new TiffBitmapEncoder()),
        };
        foreach (var (ext, encoder) in cases)
        {
            var data = Encode(encoder, src);
            ImageHeaderReader.TryGetDimensions(data, out int w, out int h).Should().BeTrue(ext);
            (w, h).Should().Be((123, 45), ext);
            ImageHeaderReader.DetectExtension(data).Should().Be(ext);
        }
    });

    [Fact]
    public Task CenterCropResize_KeepsTheCenteredRegionAtTheExactTargetSize() => _sta.RunOnStaAsync(() =>
    {
        // 900x300 -> 300x300 keeps the middle 300 columns (all green); 300x900 -> 300x300 keeps all three thirds.
        var wide = ImagePacker.CenterCropResize(Thirds(900, 300), 300, 300);
        wide.IsFrozen.Should().BeTrue();
        (wide.PixelWidth, wide.PixelHeight).Should().Be((300, 300));
        PixelAt(wide, 5, 150).Should().Be((0, 255, 0, 255));
        PixelAt(wide, 294, 150).Should().Be((0, 255, 0, 255));

        var square = ImagePacker.CenterCropResize(Thirds(600, 600), 150, 150);
        (square.PixelWidth, square.PixelHeight).Should().Be((150, 150));
        PixelAt(square, 10, 75).R.Should().Be(255);   // red third survives on the left
        PixelAt(square, 75, 75).G.Should().Be(255);   // green in the middle
        PixelAt(square, 140, 75).B.Should().Be(255);  // blue on the right
    });

    [Fact]
    public Task CenterCropResize_UpscalesSmallSources() => _sta.RunOnStaAsync(() =>
    {
        // 90x40 -> crop the centered 40x40 (x 25..64: thirds split at 30 and 60), so the
        // output center maps to source x=45, well inside the green third.
        var up = ImagePacker.CenterCropResize(Thirds(90, 40), 795, 795);
        (up.PixelWidth, up.PixelHeight).Should().Be((795, 795));
        PixelAt(up, 397, 397).Should().Be((0, 255, 0, 255));
    });

    private sealed class Tile : IHasMugshotImage
    {
        public ImageSource? MugshotSource { get; set; }
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }
        public bool HasMugshot { get; set; } = true;
        public bool IsVisible { get; set; } = true;
        public string ImagePath { get; set; } = string.Empty;
    }

    private static Tile TileOf(BitmapSource s) => new()
    {
        MugshotSource = s,
        OriginalPixelWidth = s.PixelWidth, OriginalPixelHeight = s.PixelHeight,
        OriginalDipWidth = s.PixelWidth, OriginalDipHeight = s.PixelHeight,
    };

    [Fact]
    public Task Packer_NormalizesOffSizeTilesToTheModeSize() => _sta.RunOnStaAsync(() =>
    {
        var a = TileOf(Thirds(100, 100));
        var b = TileOf(Thirds(100, 100));
        var odd = TileOf(Thirds(300, 150));
        var untouched = a.MugshotSource;

        new ImagePacker().FitOriginalImagesToContainer(
            new ObservableCollection<IHasMugshotImage> { a, b, odd }, 1000, 1000, 5, normalizeAndCropImages: true);

        a.MugshotSource.Should().BeSameAs(untouched);
        var normalized = (BitmapSource)odd.MugshotSource!;
        (normalized.PixelWidth, normalized.PixelHeight).Should().Be((100, 100));
        (odd.OriginalPixelWidth, odd.OriginalPixelHeight).Should().Be((100, 100));
        odd.OriginalDipDiagonal.Should().BeApproximately(Math.Sqrt(2) * 100, 1e-9);
        odd.ImageWidth.Should().Be(a.ImageWidth);
        PixelAt(normalized, 50, 50).G.Should().Be(255); // center crop of 300x150 keeps the green middle
    });

    [Fact]
    public Task Packer_LoadsFromFileWhenTileHasNoBitmap() => _sta.RunOnStaAsync(() =>
    {
        var dir = Directory.CreateTempSubdirectory("npc2-imgtest-");
        try
        {
            var path = Path.Combine(dir.FullName, "odd.png");
            File.WriteAllBytes(path, Encode(new PngBitmapEncoder(), Thirds(200, 100)));
            var a = TileOf(Thirds(50, 50));
            var b = TileOf(Thirds(50, 50));
            var fromFile = new Tile
            {
                ImagePath = path, OriginalPixelWidth = 200, OriginalPixelHeight = 100,
                OriginalDipWidth = 200, OriginalDipHeight = 100,
            };

            new ImagePacker().FitOriginalImagesToContainer(
                new ObservableCollection<IHasMugshotImage> { a, b, fromFile }, 500, 500, 0, normalizeAndCropImages: true);

            var normalized = (BitmapSource)fromFile.MugshotSource!;
            (normalized.PixelWidth, normalized.PixelHeight).Should().Be((50, 50));
            // File released after load (stream-based load, no lingering handle).
            File.Delete(path);
            File.Exists(path).Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    });
}
