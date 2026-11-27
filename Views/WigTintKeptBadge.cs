using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NPC_Plugin_Chooser_2.Views;

/// <summary>
/// The mugshot tile's "wig hair colour kept for this NPC" badge: the tile's wig icon
/// (<c>Resources/Has Wig.png</c>) with its right half in <see cref="AccentBrush"/>. It takes the
/// place of the plain wig icon on a tile whose NPC has a kept choice, so it is drawn to match
/// that icon: the WHOLE icon file, margins included, fitted and centred as an Image with
/// Stretch=Uniform fits it, which puts the wig exactly where, and exactly as large as, the plain
/// icon's. The left half is the icon as it is (its own dark brown); the right half is the same
/// pixels recoloured, their transparency kept.
///
/// <para>The white glow that lifts the icon off the mugshot is not drawn here: NpcsView.xaml
/// gives this element the same effect it gives the plain icon.</para>
/// </summary>
public sealed class WigTintKeptBadge : FrameworkElement
{
    public const string IconUri = "pack://application:,,,/NPC Plugin Chooser 2;component/Resources/Has Wig.png";

    /// <summary>The right half's colour unless <see cref="AccentBrush"/> says otherwise. A fixed
    /// colour, like the tile's other badges (they lie over a mugshot, not over a theme surface):
    /// dark enough to show on the white glow, and far from the icon's brown.</summary>
    public static readonly Color KeptGreen = Color.FromRgb(0x10, 0x9A, 0x30);

    /// <summary>The icon is decoded at this width: well above any size a tile draws it at, far
    /// below the file's 1254 pixels.</summary>
    private const int DecodePixels = 256;

    private static readonly Brush DefaultAccentBrush = Frozen(new SolidColorBrush(KeptGreen));
    private static readonly Brush FallbackBaseBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x54, 0x24, 0x02)));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(WigTintKeptBadge),
        new FrameworkPropertyMetadata(DefaultAccentBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the right half: the kept hair colour. Only a solid colour is used;
    /// any other brush draws <see cref="KeptGreen"/>.</summary>
    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public WigTintKeptBadge()
    {
        // The icon is scaled down several times over: averaged, not sampled.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static BitmapSource? _icon;
    private static readonly Dictionary<Color, BitmapSource> Recoloured = new();

    /// <summary>The wig icon, or null when it cannot be loaded (the badge then draws two plain
    /// halves rather than nothing, and tries again at the next draw).</summary>
    private static BitmapSource? Icon()
    {
        if (_icon != null) return _icon;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(IconUri, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = DecodePixels;
            image.EndInit();
            image.Freeze();
            _icon = image;
        }
        catch (Exception)
        {
            _icon = null;
        }
        return _icon;
    }

    /// <summary>The icon with every pixel's colour replaced by <paramref name="colour"/> and its
    /// transparency kept: the wig's lines in that colour. One bitmap per colour, kept.</summary>
    private static BitmapSource Recolour(BitmapSource icon, Color colour)
    {
        if (Recoloured.TryGetValue(colour, out var known)) return known;

        var source = new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = colour.B;
            pixels[i + 1] = colour.G;
            pixels[i + 2] = colour.R;
            // pixels[i + 3], the transparency, stays: it is the wig's shape.
        }

        var recoloured = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        recoloured.Freeze();
        Recoloured[colour] = recoloured;
        return recoloured;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // The icon file is square: the largest centred square, as Stretch=Uniform gives an Image.
        double side = Math.Min(width, height);
        var iconBox = new Rect((width - side) / 2, (height - side) / 2, side, side);
        double middle = width / 2;
        var leftHalf = new Rect(0, 0, middle, height);
        var rightHalf = new Rect(middle, 0, width - middle, height);
        var accent = AccentBrush is SolidColorBrush solid ? solid.Color : KeptGreen;

        var icon = Icon();
        if (icon == null)
        {
            var block = new Rect(iconBox.X + side * 0.25, iconBox.Y + side * 0.25, side * 0.5, side * 0.5);
            drawingContext.DrawRectangle(FallbackBaseBrush, null, Rect.Intersect(block, leftHalf));
            drawingContext.DrawRectangle(new SolidColorBrush(accent), null, Rect.Intersect(block, rightHalf));
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(leftHalf));
        drawingContext.DrawImage(icon, iconBox);
        drawingContext.Pop();

        drawingContext.PushClip(new RectangleGeometry(rightHalf));
        drawingContext.DrawImage(Recolour(icon, accent), iconBox);
        drawingContext.Pop();
    }
}
