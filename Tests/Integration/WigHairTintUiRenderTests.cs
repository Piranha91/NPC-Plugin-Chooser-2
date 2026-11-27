using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Renders the converted-wig hair colour UI to PNG files so that someone who cannot run the app
/// can look at it: a mugshot tile's badge strip with the plain wig icon beside one with the
/// "kept" badge, once per theme, and the 3D preview's toggle and Keep box. The files go to the
/// folder named by the <c>NPC2_UI_TRACE_DIR</c> environment variable, or to <c>ui-traces</c>
/// beside the test assembly, like <see cref="WigHeadgearUiRenderTests"/>; their names start with
/// <c>tint-</c>.
///
/// <para>What the pictures are and are not. The badge is the real
/// <see cref="WigTintKeptBadge"/> control, in a square of the size NpcsView.xaml gives every
/// info badge of a tile and with the same white glow; the icons beside it are the real icon
/// files drawn the way the tile draws them. The strip around them is rebuilt here (the real tile
/// template needs the whole view), over a flat colour where the mugshot would be. The
/// toggle panel is NOT the real UC_InternalMugshotPreview XAML, which cannot be created without a
/// graphics context: it is rebuilt here control for control, with its texts taken from a real
/// <see cref="VM_WigHairTintPreview"/>, over a flat backdrop where the 3D view would be.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHairTintUiRenderTests
{
    private static readonly string[] Themes = { "Dark", "Light", "GitHub Light", "Tokyo Night" };

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _output;

    public WigHairTintUiRenderTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _output = output;
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing (as WigHeadgearUiRenderTests)
    // ---------------------------------------------------------------------------------------------

    private static string OutputFolder()
    {
        var folder = Environment.GetEnvironmentVariable("NPC2_UI_TRACE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(AppContext.BaseDirectory, "ui-traces");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string FileName(string what, string theme) => $"{what}-{theme.Replace(' ', '-')}.png";

    private static RenderTargetBitmap Render(FrameworkElement root)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static List<Color> Pixels(BitmapSource bitmap, Rect area)
    {
        int left = Math.Max(0, (int)Math.Ceiling(area.Left)), top = Math.Max(0, (int)Math.Ceiling(area.Top));
        int width = Math.Min(bitmap.PixelWidth - left, (int)Math.Floor(area.Width));
        int height = Math.Min(bitmap.PixelHeight - top, (int)Math.Floor(area.Height));
        var raw = new byte[width * height * 4];
        bitmap.CopyPixels(new Int32Rect(left, top, width, height), raw, width * 4, 0);
        var colours = new List<Color>(width * height);
        for (int i = 0; i < raw.Length; i += 4) colours.Add(Color.FromRgb(raw[i + 2], raw[i + 1], raw[i]));
        return colours;
    }

    private static bool Near(Color a, Color b, int tolerance = 10) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    // ---------------------------------------------------------------------------------------------
    // The mugshot tile's badge strip
    // ---------------------------------------------------------------------------------------------

    /// <summary>A mid-sized mugshot tile's width. NpcsView sizes every info badge of a tile to a
    /// square of 20% of the tile's width.</summary>
    private const double TileWidth = 200;
    private const double BadgeSide = TileWidth / 5;

    // Flat colours standing in for the part of the mugshot the badges lie over.
    private static readonly Color DarkMugshot = Color.FromRgb(0x2A, 0x23, 0x20);
    private static readonly Color LightMugshot = Color.FromRgb(0xC9, 0xB8, 0xA6);

    private static BitmapImage Icon(string file) =>
        new(new Uri("pack://application:,,,/NPC Plugin Chooser 2;component/Resources/" + file, UriKind.Absolute));

    /// <summary>The white glow NpcsView gives the tile's wig icon, in both of its states.</summary>
    private static DropShadowEffect Glow() =>
        new() { Color = Colors.White, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9 };

    /// <summary>An icon badge as the tile draws it: the badge square holding a Viewbox with the
    /// whole icon file.</summary>
    private static Grid IconBadge(string file, bool glow, double side = BadgeSide)
    {
        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Child = new Image { Source = Icon(file) },
        };
        if (glow) viewbox.Effect = Glow();
        var square = new Grid { Width = side, Height = side };
        square.Children.Add(viewbox);
        return square;
    }

    /// <summary>The "kept" badge as the tile draws it: the control filling the same square.</summary>
    private static Grid KeptBadge(bool glow, out WigTintKeptBadge badge, double side = BadgeSide)
    {
        badge = new WigTintKeptBadge();
        if (glow) badge.Effect = Glow();
        var square = new Grid { Width = side, Height = side };
        square.Children.Add(badge);
        return square;
    }

    /// <summary>The top left corner of a tile: its icon row, here the existing "No Antlers" badge
    /// and then the wig icon in one of its two states, over a flat colour where the mugshot
    /// would be.</summary>
    private static Border Strip(Color mugshot, bool kept)
    {
        var icons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        icons.Children.Add(IconBadge("No Antlers.png", glow: false));
        icons.Children.Add(kept ? KeptBadge(glow: true, out _) : IconBadge("Has Wig.png", glow: true));
        return new Border
        {
            Width = BadgeSide * 2 + 24, Height = BadgeSide + 12, Margin = new Thickness(0, 0, 12, 12),
            Background = new SolidColorBrush(mugshot), Child = icons, ClipToBounds = true,
        };
    }

    /// <summary>One badge square drawn alone on white (without the glow, when it was built
    /// without one), to be measured.</summary>
    private static RenderTargetBitmap DrawAlone(Grid badgeSquare) =>
        Render(new Border
        {
            Background = Brushes.White, Child = badgeSquare, UseLayoutRounding = true, SnapsToDevicePixels = true,
        });

    private static (Color[] Colours, int Width, int Height) AllPixels(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var raw = new byte[width * height * 4];
        bitmap.CopyPixels(raw, width * 4, 0);
        var colours = new Color[width * height];
        for (int i = 0; i < colours.Length; i++) colours[i] = Color.FromRgb(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4]);
        return (colours, width, height);
    }

    /// <summary>The box around the inked pixels (those noticeably darker than the white they
    /// are drawn on), edges inclusive.</summary>
    private static (int Left, int Top, int Right, int Bottom) InkBox(BitmapSource onWhite)
    {
        var (colours, width, height) = AllPixels(onWhite);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = colours[y * width + x];
                if (c.R >= 200 && c.G >= 200 && c.B >= 200) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return (left, top, right, bottom);
    }

    // The wig icon's own colour is a dark brown, about (84, 36, 2).
    private static bool IsIconBrown(Color c) => c.R is >= 50 and <= 140 && c.R > c.G + 25 && c.G >= c.B;

    // Any mix of that brown with white, and of a green with white.
    private static bool IsBrownish(Color c) => c.R > c.G + 30 && c.R < 200;
    private static bool IsGreenish(Color c) => c.G > c.R + 40 && c.G > c.B + 40;

    [Fact]
    public async Task MugshotTile_KeptBadge_BesideThePlainWigIcon_RenderedInEveryTheme()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard();
            string folder = OutputFolder();

            // --- same square, same place, same size as the icon it replaces -------------------------
            // Each badge drawn alone at the tile's actual size, without the glow, and its ink boxed.
            var plainSquare = IconBadge("Has Wig.png", glow: false);
            var plainBox = InkBox(DrawAlone(plainSquare));
            var keptSquare = KeptBadge(glow: false, out var smallBadge);
            var keptBox = InkBox(DrawAlone(keptSquare));
            var antlersBox = InkBox(DrawAlone(IconBadge("No Antlers.png", glow: false)));
            _output.WriteLine($"badge square {BadgeSide}x{BadgeSide} on a {TileWidth} wide tile; ink boxes (left, top, right, bottom): " +
                              $"plain wig {plainBox}, kept badge {keptBox}, No Antlers {antlersBox}");

            smallBadge.ActualWidth.Should().Be(BadgeSide, "the badge fills the square every info badge of a tile gets");
            smallBadge.ActualHeight.Should().Be(BadgeSide);
            var plainViewbox = (FrameworkElement)plainSquare.Children[0];
            plainViewbox.ActualWidth.Should().BeApproximately(BadgeSide, 0.01, "the plain icon fills that square too");
            plainViewbox.ActualHeight.Should().BeApproximately(BadgeSide, 0.01);

            Math.Abs(keptBox.Left - plainBox.Left).Should().BeLessThanOrEqualTo(1, "the kept badge's wig starts where the plain icon's does");
            Math.Abs(keptBox.Top - plainBox.Top).Should().BeLessThanOrEqualTo(1, "the kept badge's wig is as high up as the plain icon's");
            Math.Abs(keptBox.Right - plainBox.Right).Should().BeLessThanOrEqualTo(1, "the kept badge's wig is as wide as the plain icon's");
            Math.Abs(keptBox.Bottom - plainBox.Bottom).Should().BeLessThanOrEqualTo(1, "the kept badge's wig is as tall as the plain icon's");

            // --- the two halves, on the badge drawn five times as large -----------------------------
            var bigSquare = KeptBadge(glow: false, out var bigBadge, side: BadgeSide * 5);
            var (pixels, width, height) = AllPixels(DrawAlone(bigSquare));
            var green = ((SolidColorBrush)bigBadge.AccentBrush).Color;
            var leftHalf = new List<Color>();
            var rightHalf = new List<Color>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Two pixels either side of the middle are left out: the halves meet there.
                    if (x < width / 2 - 2) leftHalf.Add(pixels[y * width + x]);
                    else if (x >= width / 2 + 2) rightHalf.Add(pixels[y * width + x]);
                }
            }

            leftHalf.Should().Contain(c => IsIconBrown(c), "the left half is the wig icon as it is, in its own dark brown");
            leftHalf.Should().NotContain(c => IsGreenish(c), "the green stays on the right half");
            rightHalf.Should().Contain(c => Near(c, green), "the right half of the wig is drawn in the badge's green");
            rightHalf.Should().NotContain(c => IsBrownish(c), "the icon's own colour stays on the left half");

            // It is the wig's outline, not a two-coloured block: most of the badge is see-through.
            var all = leftHalf.Concat(rightHalf).ToList();
            (all.Count(c => c.R > 240 && c.G > 240 && c.B > 240) / (double)all.Count).Should().BeGreaterThan(0.4,
                "only the icon's lines are drawn");

            var brown = leftHalf.Where(IsIconBrown).OrderBy(c => c.R + c.G + c.B).First();
            NpcSelectionIndicatorRules.ColorDistance(green, Colors.White).Should().BeGreaterThan(150,
                "the green half must show on the white glow around it");
            NpcSelectionIndicatorRules.ColorDistance(green, brown).Should().BeGreaterThan(100,
                "the two halves must be told apart");

            // --- the pictures -----------------------------------------------------------------------
            foreach (var themeName in Themes)
            {
                using var theme = new WigHeadgearUiRenderTests.AppliedTheme(themeName);
                var background = theme.Brush("SecondaryBackground");
                var foreground = theme.Brush("PrimaryForeground");

                // The strips at the tile's actual size, rendered once, so that the enlargement
                // below shows these very pixels and not a second, finer drawing.
                var strips = new StackPanel { UseLayoutRounding = true, SnapsToDevicePixels = true };
                foreach (var mugshot in new[] { DarkMugshot, LightMugshot })
                {
                    var pair = new StackPanel { Orientation = Orientation.Horizontal };
                    pair.Children.Add(Strip(mugshot, kept: false));
                    pair.Children.Add(Strip(mugshot, kept: true));
                    strips.Children.Add(pair);
                }
                var actual = Render(strips);

                var actualSize = new Image
                {
                    Source = actual, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left,
                };
                var enlarged = new Image
                {
                    Source = actual, Width = actual.PixelWidth * 4, Height = actual.PixelHeight * 4,
                    Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 16, 0, 0),
                };
                RenderOptions.SetBitmapScalingMode(enlarged, BitmapScalingMode.NearestNeighbor);

                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = $"Mugshot tile: kept wig hair colour badge, theme '{themeName}'",
                    FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
                });
                content.Children.Add(new TextBlock
                {
                    Text = $"Each strip is the top left corner of a mugshot tile {TileWidth} wide: the existing " +
                           "\"No Antlers\" badge, then the wig icon. Left strips: the plain wig icon. Right strips: " +
                           "the hair colour is kept for this NPC (its tooltip gains the line \"" +
                           WigHairTintPreviewRules.KeptTooltip(WigHairTintMode.Always) + "\"). Top: over a dark part " +
                           $"of a mugshot. Bottom: over a light part. Every badge square is {BadgeSide} x {BadgeSide} " +
                           "(20% of the tile's width). Actual size, then the same pixels enlarged 4x. The badge " +
                           "takes no theme colours (it lies over the mugshot); only the surroundings differ.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 900, Margin = new Thickness(0, 0, 0, 10),
                });
                content.Children.Add(actualSize);
                content.Children.Add(enlarged);

                var root = new Border
                {
                    Background = background, Padding = new Thickness(16), Child = content,
                    UseLayoutRounding = true, SnapsToDevicePixels = true,
                };
                System.Windows.Documents.TextElement.SetForeground(root, foreground);

                var bitmap = Render(root);
                string path = Path.Combine(folder, FileName("tint-tile-badge", themeName));
                SavePng(bitmap, path);
                _output.WriteLine($"wrote {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");

                bitmap.PixelWidth.Should().BeGreaterThan(actual.PixelWidth * 4, $"[{themeName}] the enlargement is in the picture");
                bitmap.PixelHeight.Should().BeGreaterThan(actual.PixelHeight * 5, $"[{themeName}] below the actual size strips");
            }
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The 3D preview's toggle and Keep box
    // ---------------------------------------------------------------------------------------------

    private static readonly RaceMenuHairTintState RaceMenuFound =
        new(true, true, 2050, @"SKSE\Plugins\skee64.dll", @"X:\Data\SKSE\Plugins\skee64.dll", Array.Empty<string>());

    /// <summary>
    /// The panel's markup, copied from UC_InternalMugshotPreview.xaml (its WigTintPanel) without
    /// the visibility binding and the tooltips: the same controls and the same bindings, so
    /// parsing it here also checks that the bindings reach the view model. {0} is the radio
    /// group's name (two panels share one picture).
    /// </summary>
    private const string TogglePanelXaml = @"
<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        HorizontalAlignment=""Left"" Background=""#A0000000"" CornerRadius=""4"" Padding=""8,6"">
    <StackPanel Orientation=""Vertical"">
        <TextBlock Text=""Converted wig hair color"" Foreground=""White""/>
        <StackPanel Orientation=""Horizontal"" Margin=""0,4,0,0"">
            <RadioButton GroupName=""{0}"" VerticalContentAlignment=""Center"" IsChecked=""{{Binding IsAuto}}"">
                <TextBlock Text=""Auto"" Foreground=""White""/>
            </RadioButton>
            <RadioButton GroupName=""{0}"" VerticalContentAlignment=""Center"" Margin=""10,0,0,0"" IsChecked=""{{Binding IsOn}}"">
                <TextBlock Text=""On"" Foreground=""White""/>
            </RadioButton>
            <RadioButton GroupName=""{0}"" VerticalContentAlignment=""Center"" Margin=""10,0,0,0"" IsChecked=""{{Binding IsOff}}"">
                <TextBlock Text=""Off"" Foreground=""White""/>
            </RadioButton>
        </StackPanel>
        <TextBlock Text=""{{Binding AutoCaption}}"" Foreground=""#CCCCCC"" FontSize=""11"" Margin=""0,4,0,0""/>
        <CheckBox Margin=""0,4,0,0"" IsChecked=""{{Binding Keep}}"">
            <TextBlock Text=""Keep for this NPC"" Foreground=""White""/>
        </CheckBox>
    </StackPanel>
</Border>";

    private static Border BuildTogglePanel(VM_WigHairTintPreview vm, string groupName)
    {
        var panel = (Border)System.Windows.Markup.XamlReader.Parse(string.Format(TogglePanelXaml, groupName));
        panel.DataContext = vm;
        return panel;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>The panel is dark in every theme (it lies over the 3D view), so its texts must be
    /// light whatever the theme's own text colour is: rendered once per theme to show it.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("GitHub Light")]
    [InlineData("Tokyo Night")]
    public async Task PreviewToggle_AndKeep_Rendered(string themeName)
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard();
            using var theme = new WigHeadgearUiRenderTests.AppliedTheme(themeName);
            var mod = new ModSetting
            {
                DisplayName = "High Poly NPC Overhaul",
                DetectedWigArmatures = { MutagenFixtures.Fk("0008A9:HighPoly.esp") },
            };
            var npc = MutagenFixtures.Fk("013261:Skyrim.esm");

            // As opened: Auto, with RaceMenu not found.
            using var opened = new VM_WigHairTintPreview(new Settings(),
                () => RaceMenuHairTintState.NotInstalled(@"SKSE\Plugins\skee64.dll"));
            opened.Load(mod, npc, eligible: true, 2, 2);

            // To be flipped to On and kept, with RaceMenu found: through the controls, below.
            var keptSettings = new Settings();
            using var kept = new VM_WigHairTintPreview(keptSettings, () => RaceMenuFound);
            kept.Load(mod, npc, eligible: true, 2, 2);

            opened.IsAvailable.Should().BeTrue();
            opened.AutoCaption.Should().Be("Auto: not tinted, RaceMenu not found");
            kept.AutoCaption.Should().Be("Auto: tinted, RaceMenu found");

            var openedPanel = BuildTogglePanel(opened, "a");
            var keptPanel = BuildTogglePanel(kept, "b");

            StackPanel Captioned(string caption, Border panel)
            {
                var column = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
                column.Children.Add(new TextBlock
                {
                    Text = caption, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 6),
                });
                column.Children.Add(panel);
                return column;
            }

            var columns = new StackPanel { Orientation = Orientation.Horizontal };
            columns.Children.Add(Captioned("As opened (mode Auto, RaceMenu not found)", openedPanel));
            columns.Children.Add(Captioned("Flipped to On, kept (RaceMenu found)", keptPanel));

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = $"3D preview: converted wig hair colour toggle and Keep box (top left of the view, theme '{themeName}')",
                Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });
            content.Children.Add(columns);

            // The viewport's own backdrop colour, where the NPC would be drawn.
            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), Padding = new Thickness(16),
                Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true,
            };

            Render(root); // a first layout pass, so the bindings have their values
            DoEvents();

            // --- the markup's bindings, driven the way a click drives them ----------------------
            var openedButtons = Descendants<RadioButton>(openedPanel).ToList();
            openedButtons.Select(b => ((TextBlock)b.Content).Text).Should().Equal("Auto", "On", "Off");
            openedButtons.Should().OnlyContain(b => ((SolidColorBrush)((TextBlock)b.Content).Foreground).Color == Colors.White,
                "the labels are white on the dark panel whatever the theme's text colour is");
            openedButtons.Select(b => b.IsChecked).Should().Equal(new bool?[] { true, false, false },
                "the toggle shows the view model's position");
            Descendants<TextBlock>(openedPanel).Select(t => t.Text).Should().Contain("Auto: not tinted, RaceMenu not found");
            Descendants<CheckBox>(openedPanel).Single().IsChecked.Should().BeFalse();

            var keptButtons = Descendants<RadioButton>(keptPanel).ToList();
            keptButtons[1].SetCurrentValue(ToggleButton.IsCheckedProperty, true); // click "On"
            DoEvents();
            kept.Mode.Should().Be(WigHairTintMode.Always, "checking 'On' flips the view model");
            keptButtons.Select(b => b.IsChecked).Should().Equal(new bool?[] { false, true, false });
            keptSettings.NpcWigHairTintModes.Should().BeEmpty("a flip without Keep stores nothing");

            Descendants<CheckBox>(keptPanel).Single().SetCurrentValue(ToggleButton.IsCheckedProperty, true); // tick Keep
            DoEvents();
            kept.Keep.Should().BeTrue();
            keptSettings.GetNpcWigHairTintMode(npc).Should().Be(WigHairTintMode.Always, "ticking Keep stores the position");

            var bitmap = Render(root);
            string path = Path.Combine(OutputFolder(), FileName("tint-preview-toggle", themeName));
            SavePng(bitmap, path);
            _output.WriteLine($"wrote {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");

            // The labels must be read on the dark panel in every theme: light pixels where each stands.
            var labels = Descendants<RadioButton>(openedPanel).Select(b => (TextBlock)b.Content)
                .Append((TextBlock)Descendants<CheckBox>(openedPanel).Single().Content);
            foreach (var label in labels)
            {
                var box = label.TransformToAncestor(root)
                    .TransformBounds(new Rect(0, 0, label.ActualWidth, label.ActualHeight));
                Pixels(bitmap, box).Should().Contain(c => c.R > 200 && c.G > 200 && c.B > 200,
                    $"[{themeName}] the label '{label.Text}' is drawn in white");
            }

            bitmap.PixelWidth.Should().BeGreaterThan(300);
            bitmap.PixelHeight.Should().BeGreaterThan(100);
        });
    }
}
