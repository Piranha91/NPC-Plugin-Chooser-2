using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using NPC_Plugin_Chooser_2.Views;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Renders the two new pieces of UI to PNG files, once per theme, so that someone who cannot run
/// the app can look at them: the NPC row's selection indicator in its five states, and the
/// wig-and-headgear window with two groups. The files go to the folder named by the
/// <c>NPC2_UI_TRACE_DIR</c> environment variable, or to <c>ui-traces</c> beside the test
/// assembly when it is not set.
///
/// <para>What the pictures are and are not. The window is the real
/// <see cref="WigHeadgearConflictWindow"/> XAML with its real view model, shown off-screen; its
/// title bar is drawn by the system and is not in the picture. The NPC row is NOT the real
/// NpcsView item template (that needs the whole view): it is rebuilt here from the production
/// brush (<see cref="NpcSelectionIndicatorRules.CreateHatchedBrush"/>), the theme's own colours
/// and the bar widths NpcsView.xaml uses, so it shows the colours and the hatching, not the
/// surrounding list.</para>
///
/// <para>The tests also assert what can be asserted on pixels and on the tree: solid bars are
/// one flat colour, hatched bars carry both colours, the window lists every row, has its four
/// buttons in order, and does not realise a row per NPC when the list is long.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHeadgearUiRenderTests
{
    private static readonly string[] Themes = { "Dark", "Light", "GitHub Light", "Tokyo Night" };

    private readonly WpfStaFixture _sta;
    private readonly ITestOutputHelper _output;

    public WigHeadgearUiRenderTests(WpfStaFixture sta, ITestOutputHelper output)
    {
        _sta = sta;
        _output = output;
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    private static string OutputFolder()
    {
        var folder = Environment.GetEnvironmentVariable("NPC2_UI_TRACE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(AppContext.BaseDirectory, "ui-traces");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string FileName(string what, string theme) => $"{what}-{theme.Replace(' ', '-')}.png";

    /// <summary>Loads a theme file the way ThemeManager does, and merges it into the test
    /// application's resources until disposed (ThemeManager itself would leave it applied for
    /// every later test in the run).</summary>
    internal sealed class AppliedTheme : IDisposable
    {
        public ResourceDictionary Resources { get; }

        public AppliedTheme(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Themes", name + ".xaml");
            using var stream = File.OpenRead(path);
            Resources = (ResourceDictionary)XamlReader.Load(stream);
            Application.Current.Resources.MergedDictionaries.Add(Resources);
        }

        public SolidColorBrush Brush(string key) => (SolidColorBrush)Resources[key];

        public void Dispose() => Application.Current.Resources.MergedDictionaries.Remove(Resources);
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static RenderTargetBitmap Render(Visual visual, int width, int height, Brush? background = null)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        if (background != null)
        {
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(backdrop);
        }
        bitmap.Render(visual);
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

    private static Color PixelAt(BitmapSource bitmap, int x, int y)
    {
        var px = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]); // Pbgra32; the bars are opaque
    }

    private static bool Near(Color a, Color b, int tolerance = 6) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    // ---------------------------------------------------------------------------------------------
    // The NPC row
    // ---------------------------------------------------------------------------------------------

    private sealed record RowState(string Label, Brush Bar, SolidColorBrush Solid, bool Hatched, Color? Hatch);

    private static List<RowState> RowStates(AppliedTheme theme)
    {
        var green = theme.Brush(NpcSelectionIndicatorRules.ValidBrushKey);
        var red = theme.Brush(NpcSelectionIndicatorRules.NotInLoadOrderBrushKey);
        var purple = theme.Brush(NpcSelectionIndicatorRules.NoDataBrushKey);
        // The theme's orange, or the fixed one when the theme's would not show against its green.
        var orange = NpcSelectionIndicatorRules.ResolveOutfitNoteHatchColor(green.Color,
            (theme.Resources[NpcSelectionIndicatorRules.OutfitNoteHatchBrushKey] as SolidColorBrush)?.Color);

        return new List<RowState>
        {
            new("Red: the NPC is not in the load order", red, red, false, null),
            new("Purple: the selected mod has no data", purple, purple, false, null),
            new("Green: valid selection", green, green, false, null),
            new("Green, orange hatches: valid; Include Outfit on a template-dressed NPC",
                NpcSelectionIndicatorRules.CreateHatchedBrush(green.Color, orange), green, true, orange),
            new("Green, red hatches: valid; wig and headgear on the same slot",
                NpcSelectionIndicatorRules.CreateHatchedBrush(green.Color, red.Color), green, true, red.Color),
        };
    }

    /// <summary>One row as NpcsView draws its indicator: a left bar (3 wide, or 6 for a hatched
    /// state, with the padding making up the difference) in "Bar" style; coloured text, plus the
    /// bar for a hatched state only, in "Text Color" style.</summary>
    private static Border BuildRow(RowState state, bool textColorStyle, out Border bar)
    {
        bool drawBar = !textColorStyle || state.Hatched;
        double barWidth = !drawBar ? 0
            : state.Hatched ? NpcSelectionIndicatorRules.HatchedBarWidth
            : NpcSelectionIndicatorRules.SolidBarWidth;
        double padding = !drawBar ? 0 : 9 - barWidth;

        var text = new TextBlock { Text = state.Label };
        if (textColorStyle) text.Foreground = state.Solid;

        bar = new Border
        {
            BorderBrush = state.Bar,
            BorderThickness = new Thickness(barWidth, 0, 0, 0),
            Padding = new Thickness(padding, 0, 0, 0),
            Child = text,
        };

        // The ListBoxItem around it: the default item padding.
        return new Border { Padding = new Thickness(4, 1, 4, 1), Child = bar };
    }

    [Fact]
    public async Task NpcRowIndicator_FiveStates_RenderedInEveryTheme()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard();
            string folder = OutputFolder();

            foreach (var themeName in Themes)
            {
                using var theme = new AppliedTheme(themeName);
                var states = RowStates(theme);
                var listBackground = theme.Brush("SecondaryBackground");
                var foreground = theme.Brush("PrimaryForeground");

                var bars = new List<Border>();

                StackPanel Column(string heading, bool textColorStyle, bool keepBars)
                {
                    var column = new StackPanel { Margin = new Thickness(0, 0, 28, 0) };
                    column.Children.Add(new TextBlock
                    {
                        Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
                    });
                    foreach (var state in states)
                    {
                        column.Children.Add(BuildRow(state, textColorStyle, out var bar));
                        if (keepBars) bars.Add(bar);
                    }
                    return column;
                }

                var columns = new StackPanel { Orientation = Orientation.Horizontal };
                columns.Children.Add(Column("Indicator style: Bar", textColorStyle: false, keepBars: true));
                columns.Children.Add(Column("Indicator style: Text Color", textColorStyle: true, keepBars: false));

                var zoomed = Column("Indicator style: Bar, enlarged 4x", textColorStyle: false, keepBars: false);
                zoomed.LayoutTransform = new ScaleTransform(4, 4);
                zoomed.Margin = new Thickness(0, 18, 0, 0);

                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = $"NPC list selection indicator, theme '{themeName}'",
                    FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
                });
                content.Children.Add(columns);
                content.Children.Add(zoomed);

                var root = new Border
                {
                    Background = listBackground, Padding = new Thickness(16), Child = content,
                    // Whole-pixel layout, so a bar's pixels are the bar's and not a blend with the row above.
                    UseLayoutRounding = true, SnapsToDevicePixels = true,
                };
                TextElement_SetForeground(root, foreground);

                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                root.Arrange(new Rect(root.DesiredSize));
                root.UpdateLayout();

                int width = (int)Math.Ceiling(root.ActualWidth);
                int height = (int)Math.Ceiling(root.ActualHeight);
                var bitmap = Render(root, width, height);

                string path = Path.Combine(folder, FileName("npc-row-indicator", themeName));
                SavePng(bitmap, path);
                _output.WriteLine($"wrote {path} ({width}x{height})");

                // --- what the pixels must show -----------------------------------------------------
                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    var bar = bars[i];
                    var origin = bar.TranslatePoint(new Point(0, 0), root);
                    int left = (int)Math.Round(origin.X);
                    int top = (int)Math.Round(origin.Y);
                    int barWidth = (int)bar.BorderThickness.Left;
                    int barHeight = (int)Math.Floor(bar.ActualHeight);

                    barWidth.Should().Be(state.Hatched ? 6 : 3, $"[{themeName}] {state.Label}");
                    (bar.BorderThickness.Left + bar.Padding.Left).Should().Be(9,
                        "the name starts at the same place in every state");

                    var seen = new List<Color>();
                    for (int y = top + 1; y < top + barHeight - 1; y++)
                    for (int x = left; x < left + barWidth; x++)
                        seen.Add(PixelAt(bitmap, x, y));

                    if (!state.Hatched)
                    {
                        seen.Should().OnlyContain(c => c == state.Solid.Color,
                            $"[{themeName}] '{state.Label}' is one flat colour, exactly the theme's, as before");
                    }
                    else
                    {
                        seen.Should().Contain(c => Near(c, state.Solid.Color),
                            $"[{themeName}] '{state.Label}' keeps the green of a valid selection");
                        var hatch = state.Hatch!.Value;
                        seen.Should().Contain(c => Near(c, hatch),
                            $"[{themeName}] '{state.Label}' carries its hatch colour");
                        double share = seen.Count(c => Near(c, hatch)) / (double)seen.Count;
                        share.Should().BeInRange(0.25, 0.75, "the two colours share the bar about evenly");
                        NpcSelectionIndicatorRules.ColorDistance(hatch, state.Solid.Color).Should().BeGreaterThan(100,
                            $"[{themeName}] the hatch colour must stand out against the green it is striped with");
                    }
                }
            }
        });
    }

    /// <summary>Text in an element built outside a window still needs a foreground; the theme's
    /// implicit TextBlock style supplies it when the theme is merged, this covers the rest.</summary>
    private static void TextElement_SetForeground(DependencyObject root, Brush foreground) =>
        System.Windows.Documents.TextElement.SetForeground(root, foreground);

    // ---------------------------------------------------------------------------------------------
    // The window
    // ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<WigHeadgearConflictCase> TwoGroups()
    {
        var officer = WigHeadgearConflictUiTests.OfficerConflict();
        var guard = WigHeadgearConflictUiTests.GuardConflict();
        const string mod = "High Poly NPC Overhaul";

        return new[]
        {
            new WigHeadgearConflictCase(MutagenFixtures.Fk("01327A:Skyrim.esm"), "Legate Rikke | LegateRikke | 01327A:Skyrim.esm", mod, new[] { officer }),
            new WigHeadgearConflictCase(MutagenFixtures.Fk("0E1BA8:Skyrim.esm"), "Legate Rikke | CWBattleRikke | 0E1BA8:Skyrim.esm", mod, new[] { officer }),
            new WigHeadgearConflictCase(MutagenFixtures.Fk("0A1001:Skyrim.esm"), "Whiterun Guard | WhiterunGuard01 | 0A1001:Skyrim.esm", mod, new[] { guard }),
            new WigHeadgearConflictCase(MutagenFixtures.Fk("0A1002:Skyrim.esm"), "Whiterun Guard | WhiterunGuard02 | 0A1002:Skyrim.esm", mod, new[] { guard }),
            new WigHeadgearConflictCase(MutagenFixtures.Fk("0A1003:Skyrim.esm"), "Whiterun Guard | WhiterunGuard03 | 0A1003:Skyrim.esm", mod, new[] { guard }),
        };
    }

    private static WigHeadgearConflictWindow ShowOffScreen(VM_WigHeadgearConflictWindow vm)
    {
        var window = new WigHeadgearConflictWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        DoEvents();
        return window;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public async Task ConflictWindow_TwoGroups_RenderedInEveryTheme()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            string folder = OutputFolder();

            foreach (var themeName in Themes)
            {
                using var theme = new AppliedTheme(themeName);
                using var vm = new VM_WigHeadgearConflictWindow(TwoGroups());
                var window = ShowOffScreen(vm);
                try
                {
                    window.Title.Should().Be("Wig and headgear on the same slot");

                    // The window itself is the visual (its content alone would be drawn shifted by
                    // its own margin); the template root gives the size of the client area.
                    var content = (FrameworkElement)window.Content;
                    var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                    int width = (int)Math.Ceiling(client.ActualWidth);
                    int height = (int)Math.Ceiling(client.ActualHeight);
                    width.Should().BeGreaterThan(600);
                    height.Should().BeGreaterThan(300);

                    var bitmap = Render(window, width, height, theme.Brush("PrimaryBackground"));
                    string path = Path.Combine(folder, FileName("wig-headgear-window", themeName));
                    SavePng(bitmap, path);
                    _output.WriteLine($"wrote {path} ({width}x{height})");

                    // --- what the tree must hold --------------------------------------------------
                    var texts = Descendants<TextBlock>(content).Select(t => t.Text).ToList();
                    texts.Should().Contain(VM_WigHeadgearConflictWindow.ExplanationText);
                    texts.Should().Contain("5 NPCs in 2 groups");
                    texts.Should().Contain(
                        "Wig 'HighPoly_WigAA_HairMaleNord01' (slot 31) would be added to outfit 'GuardWhiterunOutfit', " +
                        "which holds 'Whiterun Guard's Helmet (ArmorGuardHelmetWhiterun)' (slots 31 and 42); both use slot 31");
                    texts.Should().Contain("Legate Rikke | CWBattleRikke | 0E1BA8:Skyrim.esm");
                    texts.Should().Contain("3 NPCs").And.Contain("2 NPCs");

                    var list = Descendants<ListBox>(content).Single();
                    list.Items.Count.Should().Be(7, "two group rows and five NPC rows");

                    Descendants<Button>(content).Select(b => b.Content as string).Should().Equal(
                        "Continue (forward the wig)", "Continue (omit the wig)", "Convert to head parts", "Cancel run");
                    Descendants<Button>(content).Should().NotContain(b => b.IsDefault, "Enter must not choose for the user");

                    var remember = Descendants<CheckBox>(content).Single();
                    remember.Content.Should().Be("Remember this choice for these NPCs");
                    remember.IsChecked.Should().BeFalse();
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    [Fact]
    public async Task ConflictWindow_WithHundredsOfNpcs_DoesNotBuildARowForEachOfThem()
    {
        await _sta.RunOnStaAsync(() =>
        {
            using var guard = new StaticStateGuard(immediateSchedulers: false);
            using var theme = new AppliedTheme("Dark");

            // Every helmeted guard of a full load order: 40 outfits' worth of groups, 1,200 NPCs.
            var cases = new List<WigHeadgearConflictCase>();
            for (int i = 0; i < 1200; i++)
            {
                int group = i % 40;
                var conflict = WigHeadgearConflictUiTests.Conflict(
                    $"{0x0B0000 + group:X6}:Skyrim.esm", $"GuardOutfit{group:00}",
                    $"{0x0C0000 + group:X6}:Skyrim.esm", $"ArmorGuardHelmet{group:00}", $"Guard Helmet {group:00}",
                    "000802:High Poly NPC Overhaul.esp", "HighPoly_WigAA_HairMaleNord01");
                cases.Add(new WigHeadgearConflictCase(MutagenFixtures.Fk($"{0x100000 + i:X6}:Skyrim.esm"),
                    $"Guard {i:0000} | {0x100000 + i:X6}:Skyrim.esm", "High Poly NPC Overhaul", new[] { conflict }));
            }

            using var vm = new VM_WigHeadgearConflictWindow(cases);
            vm.TotalsText.Should().Be("1200 NPCs in 40 groups");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var window = ShowOffScreen(vm);
            sw.Stop();
            try
            {
                var list = Descendants<ListBox>((FrameworkElement)window.Content).Single();
                list.Items.Count.Should().Be(1240);

                int realised = Enumerable.Range(0, list.Items.Count)
                    .Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                _output.WriteLine($"1240 rows: {realised} realised; window shown and laid out in {sw.ElapsedMilliseconds} ms");

                realised.Should().BeGreaterThan(0);
                realised.Should().BeLessThan(200, "the list virtualises: only the rows in view (and a few beside) exist");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
