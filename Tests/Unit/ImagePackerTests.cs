using System.Collections.ObjectModel;
using System.Windows.Media;
using FluentAssertions;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="ImagePacker"/> packing geometry — the deterministic, dispatcher-free surface:
/// <see cref="ImagePacker.FitOriginalImagesToContainer"/> driven with <c>normalizeAndCropImages:false</c>
/// (the only mode that never touches real bitmaps), plus the private binary-search helpers
/// <c>CalculatePackerScale</c> / <c>CanPackAll</c> reached via <see cref="Reflect"/>, and the public
/// static <see cref="ImagePacker.GetImageDimensions"/>. No WPF dispatcher is required: these are pure math.
///
/// NOTE: the normalize/crop branch (normalizeAndCropImages:true) is not covered — it decodes/encodes
/// real bitmaps through ImageSharp + WPF (LoadImageFromSource / ImageToImageSource) and only matters
/// when images differ from the mode pixel size; that requires real image assets and a STA-ish WPF path,
/// which belongs to the integration wave, not this pure-unit file.
/// </summary>
public class ImagePackerTests
{
    /// <summary>Minimal in-memory <see cref="IHasMugshotImage"/> with all sizing fields settable.</summary>
    private sealed class FakeImage : IHasMugshotImage
    {
        public ImageSource? MugshotSource { get; set; }
        public double ImageWidth { get; set; }
        public double ImageHeight { get; set; }
        public int OriginalPixelWidth { get; set; }
        public int OriginalPixelHeight { get; set; }
        public double OriginalDipWidth { get; set; }
        public double OriginalDipHeight { get; set; }
        public double OriginalDipDiagonal { get; set; }
        public bool HasMugshot { get; set; }
        public bool IsVisible { get; set; } = true;
        public string ImagePath { get; set; } = string.Empty;
    }

    private static FakeImage Img(double w, double h, bool visible = true) => new()
    {
        OriginalDipWidth = w,
        OriginalDipHeight = h,
        // Pixel dims mirror DIP dims so the (unused-here) normalize path stays internally consistent.
        OriginalPixelWidth = (int)w,
        OriginalPixelHeight = (int)h,
        IsVisible = visible,
    };

    private static ObservableCollection<IHasMugshotImage> Coll(params IHasMugshotImage[] items) =>
        new(items);

    /// <summary>
    /// Runs the public packer in its no-decode mode and captures the <see cref="PackingResult"/>
    /// raised by the <see cref="ImagePacker.PackingCompleted"/> event.
    /// </summary>
    private static (double scale, PackingResult? result) Pack(
        ImagePacker packer,
        ObservableCollection<IHasMugshotImage> images,
        double availableHeight, double availableWidth,
        int margin, int maxToFit = 50, CancellationToken token = default)
    {
        PackingResult? captured = null;
        void Handler(object? s, ImagePacker.PackingCompletedEventArgs e) => captured = e.Result;
        packer.PackingCompleted += Handler;
        try
        {
            var scale = packer.FitOriginalImagesToContainer(
                images, availableHeight, availableWidth, margin,
                normalizeAndCropImages: false, maxMugshotsToFit: maxToFit, token: token);
            return (scale, captured);
        }
        finally
        {
            packer.PackingCompleted -= Handler;
        }
    }

    // ---- empty / invisible / excluded sets ---------------------------------------------------

    [Fact]
    public void Empty_Collection_ReturnsScaleOne_AndFiresZeroResult()
    {
        var packer = new ImagePacker();
        var (scale, result) = Pack(packer, Coll(), availableHeight: 1000, availableWidth: 1000, margin: 0);

        scale.Should().Be(1.0);
        result.Should().Be(new PackingResult(0, 0));
    }

    [Fact]
    public void AllInvisible_TreatedAsEmpty_ZeroesSizesAndReturnsOne()
    {
        var packer = new ImagePacker();
        var a = Img(100, 100, visible: false);
        var b = Img(200, 200, visible: false);
        a.ImageWidth = 42; a.ImageHeight = 42; // pre-existing values must be cleared

        var (scale, result) = Pack(packer, Coll(a, b), 1000, 1000, margin: 0);

        scale.Should().Be(1.0);
        result.Should().Be(new PackingResult(0, 0));
        a.ImageWidth.Should().Be(0);
        a.ImageHeight.Should().Be(0);
        b.ImageWidth.Should().Be(0);
        b.ImageHeight.Should().Be(0);
    }

    [Fact]
    public void ZeroOriginalDip_ImagesAreExcludedFromVisibleSet()
    {
        var packer = new ImagePacker();
        var zeroW = Img(0, 100);   // width 0 -> excluded
        var zeroH = Img(100, 0);   // height 0 -> excluded

        var (scale, result) = Pack(packer, Coll(zeroW, zeroH), 1000, 1000, margin: 0);

        // No qualifying visible images -> empty-set path.
        scale.Should().Be(1.0);
        result.Should().Be(new PackingResult(0, 0));
        zeroW.ImageWidth.Should().Be(0);
        zeroH.ImageHeight.Should().Be(0);
    }

    [Fact]
    public void MixedVisibility_OnlyVisiblePositiveImagesCount()
    {
        var packer = new ImagePacker();
        var visible = Img(100, 100, visible: true);
        var hidden = Img(100, 100, visible: false);
        hidden.ImageWidth = 99; hidden.ImageHeight = 99;

        var (scale, _) = Pack(packer, Coll(visible, hidden), 1000, 1000, margin: 0);

        scale.Should().BeGreaterThan(0);
        // Hidden image is zeroed; visible image is scaled.
        hidden.ImageWidth.Should().Be(0);
        hidden.ImageHeight.Should().Be(0);
        visible.ImageWidth.Should().BeApproximately(100 * scale, 1e-6);
        visible.ImageHeight.Should().BeApproximately(100 * scale, 1e-6);
    }

    // ---- single image fits ------------------------------------------------------------------

    [Fact]
    public void SingleImage_FitsContainer_ScalesToFitAndReportsFinalSize()
    {
        var packer = new ImagePacker();
        var img = Img(100, 100);

        // Container 1000x1000, no margin: one 100x100 image can grow to the high cap (min(10, 10, 10)=10).
        var (scale, result) = Pack(packer, Coll(img), 1000, 1000, margin: 0);

        scale.Should().BeApproximately(10.0, 0.01); // high cap is 10.0
        img.ImageWidth.Should().BeApproximately(100 * scale, 1e-6);
        img.ImageHeight.Should().BeApproximately(100 * scale, 1e-6);
        result!.DefinitiveWidth.Should().BeApproximately(img.ImageWidth, 1e-6);
        result.DefinitiveHeight.Should().BeApproximately(img.ImageHeight, 1e-6);
    }

    [Fact]
    public void SingleImage_NonSquareContainer_LimitedByTighterAxis()
    {
        var packer = new ImagePacker();
        var img = Img(100, 50); // wide aspect

        // height 100, width 1000, no margin. effectiveH=50 -> high<=100/50=2; effectiveW=100 -> high<=1000/100=10.
        // Tighter axis (height) caps scale at 2.0.
        var (scale, _) = Pack(packer, Coll(img), availableHeight: 100, availableWidth: 1000, margin: 0);

        scale.Should().BeApproximately(2.0, 0.01);
    }

    // ---- oversized collapse -----------------------------------------------------------------

    [Fact]
    public void OversizedByMargin_CannotPack_CollapsesToZeroScale()
    {
        var packer = new ImagePacker();
        var img = Img(10, 10);

        // Margin alone (2*600 = 1200) exceeds the 1000-wide container regardless of scale,
        // so CanPackAll never succeeds and the search collapses low to 0.
        var (scale, _) = Pack(packer, Coll(img), availableHeight: 1000, availableWidth: 1000, margin: 600);

        scale.Should().Be(0.0);
        img.ImageWidth.Should().Be(0);
        img.ImageHeight.Should().Be(0);
    }

    // ---- maxMugshotsToFit limit -------------------------------------------------------------

    [Fact]
    public void MaxMugshotsToFit_LimitsImagesConsideredForScale()
    {
        var packer = new ImagePacker();
        // Many 100x100 images in a small container; capping the count to 1 lets the single
        // considered image scale large, whereas considering all of them would force a tiny scale.
        var images = Coll(Enumerable.Range(0, 20).Select(_ => (IHasMugshotImage)Img(100, 100)).ToArray());

        var (scaleCapped, _) = Pack(packer, images, availableHeight: 250, availableWidth: 250, margin: 0, maxToFit: 1);

        // One 100x100 in 250x250 -> scale 2.5 (min(10, 2.5, 2.5)).
        scaleCapped.Should().BeApproximately(2.5, 0.01);

        var (scaleAll, _) = Pack(packer, images, availableHeight: 250, availableWidth: 250, margin: 0, maxToFit: 50);
        scaleAll.Should().BeLessThan(scaleCapped, "packing all 20 images into the same box forces a smaller scale");
    }

    // ---- margin lowers scale ----------------------------------------------------------------

    [Fact]
    public void Margin_LowersAchievableScale()
    {
        var packer = new ImagePacker();

        var (noMargin, _) = Pack(packer, Coll(Img(100, 100)), 300, 300, margin: 0);
        var (withMargin, _) = Pack(packer, Coll(Img(100, 100)), 300, 300, margin: 50);

        // effective dim grows from 100 to 200, halving the cap from 3.0 to 1.5.
        noMargin.Should().BeApproximately(3.0, 0.01);
        withMargin.Should().BeApproximately(1.5, 0.01);
        withMargin.Should().BeLessThan(noMargin);
    }

    // ---- cancellation -----------------------------------------------------------------------

    [Fact]
    public void PreCancelledToken_WithVisibleImages_ThrowsOperationCanceled()
    {
        var packer = new ImagePacker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => packer.FitOriginalImagesToContainer(
            Coll(Img(100, 100)), 1000, 1000, 0,
            normalizeAndCropImages: false, maxMugshotsToFit: 50, token: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void PreCancelledToken_EmptySet_DoesNotThrow_ReturnsOne()
    {
        var packer = new ImagePacker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Empty set short-circuits before any token check, so cancellation is irrelevant here.
        double scale = packer.FitOriginalImagesToContainer(
            Coll(), 1000, 1000, 0,
            normalizeAndCropImages: false, maxMugshotsToFit: 50, token: cts.Token);

        scale.Should().Be(1.0);
    }

    // ---- CalculatePackerScale (private, via Reflect) ----------------------------------------

    [Fact]
    public void CalculatePackerScale_EmptyDimensions_ReturnsHighCap()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)>();

        // No dimensions: high stays 10.0, CanPackAll over an empty list is vacuously true,
        // so the search converges up to the cap.
        var scale = Reflect.Invoke<double>(packer, "CalculatePackerScale",
            dims, 1000.0, 1000.0, 0, CancellationToken.None);

        scale.Should().BeApproximately(10.0, 0.01);
    }

    [Fact]
    public void CalculatePackerScale_SingleSquare_MatchesContainerRatio()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)> { (100, 100) };

        var scale = Reflect.Invoke<double>(packer, "CalculatePackerScale",
            dims, 400.0, 400.0, 0, CancellationToken.None);

        // 100 -> fits 400 at scale 4.0.
        scale.Should().BeApproximately(4.0, 0.01);
    }

    [Fact]
    public void CalculatePackerScale_PreCancelled_Throws()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)> { (100, 100) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => Reflect.Invoke<double>(packer, "CalculatePackerScale",
            dims, 1000.0, 1000.0, 0, cts.Token);

        // Reflection wraps the thrown exception; assert on the inner cause.
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<OperationCanceledException>();
    }

    // ---- CanPackAll (private, via Reflect) --------------------------------------------------

    [Fact]
    public void CanPackAll_EmptyList_IsVacuouslyTrue()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)>();

        var ok = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 100.0, 0, CancellationToken.None);

        ok.Should().BeTrue();
    }

    [Fact]
    public void CanPackAll_SingleImageWithinBounds_ReturnsTrue()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)> { (50, 50) };

        // 50*1 + 0 margin = 50 <= 100 on both axes.
        var ok = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 100.0, 0, CancellationToken.None);

        ok.Should().BeTrue();
    }

    [Fact]
    public void CanPackAll_SingleImageTooWide_ReturnsFalse()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)> { (200, 50) };

        // itemW = 200 > container width 100 -> immediately false.
        var ok = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 100.0, 0, CancellationToken.None);

        ok.Should().BeFalse();
    }

    [Fact]
    public void CanPackAll_WrapsToNewRow_WhenRowExceedsWidth()
    {
        var packer = new ImagePacker();
        // Three 40x40 tiles: two fit per 100-wide row (40+40=80<=100, third wraps).
        var dims = new List<(double Width, double Height)> { (40, 40), (40, 40), (40, 40) };

        // Container 100x100: rows hold 2 tiles (height 40 each). Third wraps to row 2 (y 40..80) -> fits.
        var fits = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 100.0, 0, CancellationToken.None);
        fits.Should().BeTrue();

        // Same tiles in a 100x60 container: second row would land at y=40..80 > 60 -> cannot pack.
        var tooShort = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 60.0, 0, CancellationToken.None);
        tooShort.Should().BeFalse();
    }

    [Fact]
    public void CanPackAll_MarginCountedTwicePerAxis()
    {
        var packer = new ImagePacker();
        var dims = new List<(double Width, double Height)> { (80, 80) };

        // 80 + 2*15 = 110 > 100 width -> false once margin is added.
        var ok = Reflect.Invoke<bool>(packer, "CanPackAll",
            dims, 1.0, 100.0, 100.0, 15, CancellationToken.None);

        ok.Should().BeFalse();
    }

    // ---- GetImageDimensions (public static) -------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetImageDimensions_NullOrEmptyPath_ReturnsZeros(string? path)
    {
        var dims = ImagePacker.GetImageDimensions(path!);
        dims.Should().Be((0, 0, 0.0, 0.0));
    }

    [Fact]
    public void GetImageDimensions_MissingFile_ReturnsZeros()
    {
        using var tmp = new TempDir();
        var missing = System.IO.Path.Combine(tmp.Path, "does-not-exist.png");

        ImagePacker.GetImageDimensions(missing).Should().Be((0, 0, 0.0, 0.0));
    }

    [Fact]
    public void GetImageDimensions_EmptyFile_ReturnsZeros()
    {
        using var tmp = new TempDir();
        var empty = tmp.WriteText("empty.png", string.Empty); // zero-length file

        ImagePacker.GetImageDimensions(empty).Should().Be((0, 0, 0.0, 0.0));
    }

    [Fact]
    public void GetImageDimensions_NonImageContent_ReturnsZeros()
    {
        using var tmp = new TempDir();
        // Non-empty but not a decodable image: Image.Identify throws -> caught -> zeros.
        var notAnImage = tmp.WriteText("notanimage.png", "this is plain text, not a PNG");

        ImagePacker.GetImageDimensions(notAnImage).Should().Be((0, 0, 0.0, 0.0));
    }

    // ---- PackingCompleted event semantics ---------------------------------------------------

    [Fact]
    public void PackingCompleted_FiresOncePerCall_WithFinalDimensions()
    {
        var packer = new ImagePacker();
        var results = new List<PackingResult>();
        packer.PackingCompleted += (_, e) => results.Add(e.Result);

        var img = Img(100, 100);
        var scale = packer.FitOriginalImagesToContainer(
            Coll(img), 1000, 1000, 0, normalizeAndCropImages: false);

        results.Should().ContainSingle();
        results[0].DefinitiveWidth.Should().BeApproximately(100 * scale, 1e-6);
        results[0].DefinitiveHeight.Should().BeApproximately(100 * scale, 1e-6);
    }

    [Fact]
    public void PackingResult_RecordEquality_HoldsByValue()
    {
        new PackingResult(12.5, 30.0).Should().Be(new PackingResult(12.5, 30.0));
        new PackingResult(12.5, 30.0).Should().NotBe(new PackingResult(12.5, 31.0));
    }
}
