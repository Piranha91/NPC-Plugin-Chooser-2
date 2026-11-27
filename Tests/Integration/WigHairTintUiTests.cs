using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using ReactiveUI;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// The view models of the converted-wig hair colour controls, on the STA fixture:
/// <list type="bullet">
///   <item><see cref="VM_WigHairTintPreview"/>, the 3D preview's Auto / On / Off toggle and
///     "Keep for this NPC": offered only when eligible; starts at the NPC's own choice, else the
///     mod's, else the global default; flipping stores nothing unless Keep is checked; Keep
///     stores, follows flips and removes; and the caption says what Auto does and why.</item>
///   <item>the NPCs tab's side: a mugshot tile's wig icon becomes the half-green "kept" badge
///     with the stored choice (only on a tile that shows the wig notice), its tooltip gains a line
///     naming the mode, every shown tile of the NPC follows a change in place, and "Clear kept
///     wig hair color" on the tile's or the NPC row's menu removes the choice.</item>
/// </list>
/// What is NOT exercised here: the 3D view itself (the picture that changes when the toggle is
/// flipped needs a graphics context), the mesh resolver's part in deciding eligibility, and the
/// XAML bindings. No game install needed.
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class WigHairTintUiTests
{
    private const uint Hair = 2;     // biped slot 31
    private const uint Body = 4;     // biped slot 32

    private static readonly FormKey Npc = MutagenFixtures.Fk("013261:Skyrim.esm");
    private static readonly FormKey OtherNpc = MutagenFixtures.Fk("013262:Skyrim.esm");

    private readonly WpfStaFixture _sta;
    public WigHairTintUiTests(WpfStaFixture sta) => _sta = sta;

    private Task OnSta(Action body) => _sta.RunOnStaAsync(() =>
    {
        using var guard = new StaticStateGuard();
        body();
    });

    private static readonly RaceMenuHairTintState RaceMenuFound =
        new(true, true, 2050, @"SKSE\Plugins\skee64.dll", @"X:\Data\SKSE\Plugins\skee64.dll", Array.Empty<string>());

    private static readonly RaceMenuHairTintState RaceMenuNotFound =
        RaceMenuHairTintState.NotInstalled(@"SKSE\Plugins\skee64.dll");

    private static ModSetting ModWithWig(WigHairTintMode? modMode = null) => new()
    {
        DisplayName = "High Poly NPC Overhaul",
        DetectedWigArmatures = { MutagenFixtures.Fk("0008A9:HighPoly.esp") },
        ModWigHairTintMode = modMode,
    };

    private sealed class Rig : IDisposable
    {
        public Settings Settings = new();
        public RaceMenuHairTintState RaceMenu = RaceMenuFound;
        public int Persisted;
        public int Redraws;
        public readonly List<FormKey> Announced = new();
        public VM_WigHairTintPreview Vm;
        private readonly IDisposable _listener;

        public Rig()
        {
            Vm = new VM_WigHairTintPreview(Settings, () => RaceMenu, () => Persisted++);
            Vm.RenderInvalidated += () => Redraws++;
            _listener = MessageBus.Current.Listen<NpcWigHairTintKeptChangedMessage>()
                .Subscribe(m => Announced.Add(m.NpcFormKey));
        }

        /// <summary>Loads an eligible NPC whose wig passes RaceMenu's slot test.</summary>
        public void LoadEligible(ModSetting? mod = null, FormKey? npc = null) =>
            Vm.Load(mod ?? ModWithWig(), npc ?? Npc, eligible: true, Hair, Hair);

        public void Dispose()
        {
            _listener.Dispose();
            Vm.Dispose();
        }
    }

    // =============================================================================================
    // Eligibility: the toggle is offered only for a converted wig that can take a hair colour
    // =============================================================================================

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, null, true)]    // the mesh could not be read: offered
    [InlineData(true, true, true, false, false)]  // the wig has no HairTint shape
    [InlineData(false, true, true, true, false)]  // the wig is not converted to head parts
    [InlineData(true, false, true, true, false)]  // the NPC has no wig
    [InlineData(true, true, false, true, false)]  // none, or more than one, wig the converter would take
    public void Eligible_OnlyForAConvertedWigWithAHairTintShape(bool converts, bool hasWig, bool carrierFound,
        bool? hasHairTintShape, bool expected)
    {
        WigHairTintPreviewRules.IsEligible(converts, hasWig, carrierFound, hasHairTintShape).Should().Be(expected);
    }

    [Fact]
    public async Task Toggle_IsOfferedOnlyWhenEligible()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.IsAvailable.Should().BeFalse("nothing is loaded yet");

            rig.Vm.Load(ModWithWig(), Npc, eligible: false, Hair, Hair);
            rig.Vm.IsAvailable.Should().BeFalse();
            rig.Vm.RenderTintOverride.Should().BeNull("an ineligible NPC is drawn as the mugshot setting says, as before");

            rig.LoadEligible();
            rig.Vm.IsAvailable.Should().BeTrue();
            rig.Vm.RenderTintOverride.Should().NotBeNull();

            rig.Vm.Load(null, Npc, eligible: true, Hair, Hair);
            rig.Vm.IsAvailable.Should().BeFalse("no mod, no toggle");

            rig.LoadEligible();
            rig.Vm.Clear();
            rig.Vm.IsAvailable.Should().BeFalse();
        });
    }

    // =============================================================================================
    // Where the toggle starts: the NPC's own choice, else the mod's, else the global default
    // =============================================================================================

    [Fact]
    public async Task InitialPosition_FollowsTheNpc_ThenTheMod_ThenTheGlobalDefault()
    {
        await OnSta(() =>
        {
            using (var global = new Rig())
            {
                global.Settings.DefaultWigHairTintMode = WigHairTintMode.Never;
                global.LoadEligible();
                global.Vm.Mode.Should().Be(WigHairTintMode.Never, "global default");
                global.Vm.IsOff.Should().BeTrue();
                global.Vm.IsAuto.Should().BeFalse();
                global.Vm.Keep.Should().BeFalse();
            }

            using (var mod = new Rig())
            {
                mod.Settings.DefaultWigHairTintMode = WigHairTintMode.Never;
                mod.LoadEligible(ModWithWig(WigHairTintMode.Always));
                mod.Vm.Mode.Should().Be(WigHairTintMode.Always, "the mod's setting beats the global default");
                mod.Vm.IsOn.Should().BeTrue();
                mod.Vm.Keep.Should().BeFalse();
            }

            using (var npc = new Rig())
            {
                npc.Settings.DefaultWigHairTintMode = WigHairTintMode.Never;
                npc.Settings.SetNpcWigHairTintMode(new[] { Npc }, WigHairTintMode.Auto);
                npc.LoadEligible(ModWithWig(WigHairTintMode.Always));
                npc.Vm.Mode.Should().Be(WigHairTintMode.Auto, "the NPC's kept choice beats its mod");
                npc.Vm.IsAuto.Should().BeTrue();
                npc.Vm.Keep.Should().BeTrue("a kept choice shows as kept");
            }
        });
    }

    // =============================================================================================
    // Flipping
    // =============================================================================================

    [Fact]
    public async Task Flip_WithoutKeep_StoresNothing_AndRedraws()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.LoadEligible();
            rig.Vm.Mode.Should().Be(WigHairTintMode.Auto);

            rig.Vm.IsOn = true;
            rig.Vm.Mode.Should().Be(WigHairTintMode.Always);
            rig.Vm.IsOff = true;
            rig.Vm.Mode.Should().Be(WigHairTintMode.Never);
            rig.Vm.IsAuto = true;

            rig.Redraws.Should().Be(3, "each flip draws the NPC again");
            rig.Settings.NpcWigHairTintModes.Should().BeEmpty("flipping without Keep changes nothing persistent");
            rig.Persisted.Should().Be(0);
            rig.Announced.Should().BeEmpty();

            // A radio button being unchecked by its neighbour writes 'false': that is not a flip.
            rig.Vm.IsAuto = false;
            rig.Vm.Mode.Should().Be(WigHairTintMode.Auto);
            rig.Redraws.Should().Be(3);
        });
    }

    [Fact]
    public async Task Flip_ShowsWhatEachModeWouldBake()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.LoadEligible();

            rig.Vm.Mode = WigHairTintMode.Always;
            rig.Vm.RenderTintOverride.Should().BeTrue("On: the wig takes the NPC's hair colour");
            rig.Vm.Mode = WigHairTintMode.Never;
            rig.Vm.RenderTintOverride.Should().BeFalse("Off: the wig's authored tint");
            rig.Vm.Mode = WigHairTintMode.Auto;
            rig.Vm.RenderTintOverride.Should().BeTrue("Auto with RaceMenu found and the wig in its slots");

            rig.RaceMenu = RaceMenuNotFound;
            rig.Vm.RenderTintOverride.Should().BeFalse("Auto without RaceMenu");
            rig.Vm.Mode = WigHairTintMode.Always;
            rig.Vm.RenderTintOverride.Should().BeTrue("On needs no RaceMenu");
        });
    }

    [Fact]
    public async Task ReloadingTheSameNpc_KeepsTheFlippedPosition_AnotherNpcStartsOver()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.LoadEligible();
            rig.Vm.Mode = WigHairTintMode.Never;

            // The reload the flip itself causes, or an attire toggle, loads the same NPC again.
            rig.LoadEligible();
            rig.Vm.Mode.Should().Be(WigHairTintMode.Never, "the user's flip survives the redraw");

            rig.LoadEligible(npc: OtherNpc);
            rig.Vm.Mode.Should().Be(WigHairTintMode.Auto, "a different NPC starts at its own effective mode");
        });
    }

    // =============================================================================================
    // Keep for this NPC
    // =============================================================================================

    [Fact]
    public async Task Keep_StoresTheCurrentMode_FollowsFlips_AndUncheckingRemovesIt()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.LoadEligible();
            rig.Vm.Mode = WigHairTintMode.Always;

            rig.Vm.Keep = true;
            rig.Settings.GetNpcWigHairTintMode(Npc).Should().Be(WigHairTintMode.Always, "Keep stores the toggle's position");
            rig.Settings.NpcWigHairTintModes.Should().ContainSingle("stored for the NPC being patched only");
            rig.Persisted.Should().Be(1);
            rig.Announced.Should().Equal(Npc);

            rig.Vm.Mode = WigHairTintMode.Never;
            rig.Settings.GetNpcWigHairTintMode(Npc).Should().Be(WigHairTintMode.Never, "a flip while kept updates the stored mode");
            rig.Vm.Mode = WigHairTintMode.Auto;
            rig.Settings.GetNpcWigHairTintMode(Npc).Should().Be(WigHairTintMode.Auto, "Auto can be kept too");
            rig.Announced.Should().HaveCount(3);

            rig.Vm.Keep = false;
            rig.Settings.GetNpcWigHairTintMode(Npc).Should().BeNull("unchecking removes the NPC's own choice");
            rig.Settings.NpcWigHairTintModes.Should().BeEmpty();
            rig.Vm.Mode.Should().Be(WigHairTintMode.Auto, "the preview stays where it was");
            rig.Announced.Should().HaveCount(4);

            rig.Vm.Mode = WigHairTintMode.Always;
            rig.Settings.NpcWigHairTintModes.Should().BeEmpty("flips after unchecking store nothing again");
        });
    }

    [Fact]
    public async Task Keep_DoesNothingWhileTheToggleIsNotOffered()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.Vm.Load(ModWithWig(), Npc, eligible: false, Hair, Hair);

            rig.Vm.Keep = true;
            rig.Vm.Mode = WigHairTintMode.Always;

            rig.Settings.NpcWigHairTintModes.Should().BeEmpty();
            rig.Redraws.Should().Be(0);
        });
    }

    [Fact]
    public async Task ChoiceClearedFromTheNpcList_UnchecksKeepInAnOpenPreview()
    {
        await OnSta(() =>
        {
            using var rig = new Rig();
            rig.LoadEligible();
            rig.Vm.Mode = WigHairTintMode.Always;
            rig.Vm.Keep = true;

            // What the NPC row's "Clear kept wig hair colour" does.
            rig.Settings.RemoveNpcWigHairTintModes(new[] { Npc });
            MessageBus.Current.SendMessage(new NpcWigHairTintKeptChangedMessage(Npc));

            rig.Vm.Keep.Should().BeFalse();
            rig.Vm.Mode.Should().Be(WigHairTintMode.Always, "clearing changes what is stored, not what is shown");
            rig.Settings.NpcWigHairTintModes.Should().BeEmpty("following the clear must not store the choice again");
        });
    }

    // =============================================================================================
    // The Auto caption
    // =============================================================================================

    [Fact]
    public async Task AutoCaption_SaysWhatAutoDoesAndWhy()
    {
        await OnSta(() =>
        {
            using (var found = new Rig())
            {
                found.LoadEligible();
                found.Vm.AutoCaption.Should().Be("Auto: tinted, RaceMenu found");
            }

            using (var notFound = new Rig())
            {
                notFound.RaceMenu = RaceMenuNotFound;
                notFound.LoadEligible();
                notFound.Vm.AutoCaption.Should().Be("Auto: not tinted, RaceMenu not found");
            }

            using (var off = new Rig())
            {
                off.RaceMenu = RaceMenuFound with { TintEnabled = false };
                off.LoadEligible();
                off.Vm.AutoCaption.Should().Be("Auto: not tinted, RaceMenu's hair tint is off");
            }

            using (var outside = new Rig())
            {
                // A skin that claims only the body slot, carrying a hair-slot wig addon.
                outside.Vm.Load(ModWithWig(), Npc, eligible: true, Body, Hair);
                outside.Vm.AutoCaption.Should().Be("Auto: not tinted, the wig is outside RaceMenu's slots");
                outside.Vm.RenderTintOverride.Should().BeFalse();
            }

            using (var hidden = new Rig())
            {
                hidden.Vm.Load(ModWithWig(), Npc, eligible: false, Hair, Hair);
                hidden.Vm.AutoCaption.Should().BeEmpty();
            }
        });
    }

    [Fact]
    public void Labels_AreTheThreeModes()
    {
        WigHairTintPreviewRules.ShortLabel(WigHairTintMode.Auto).Should().Be("Auto");
        WigHairTintPreviewRules.ShortLabel(WigHairTintMode.Always).Should().Be("On");
        WigHairTintPreviewRules.ShortLabel(WigHairTintMode.Never).Should().Be("Off");

        // The dropdowns on the Settings and Mods tabs.
        HandlingModeDisplay.ToDisplayString(WigHairTintMode.Auto).Should().Be("Auto (follow RaceMenu)");
        HandlingModeDisplay.ToDisplayString(WigHairTintMode.Always).Should().Be("On");
        HandlingModeDisplay.ToDisplayString(WigHairTintMode.Never).Should().Be("Off");
    }

    // =============================================================================================
    // The NPCs tab: the mugshot tile's "kept" badge, and "Clear kept wig hair color" on the tile's
    // and the NPC row's menus
    // =============================================================================================

    /// <summary>Stands in for the wig icon's tooltip, which a real tile works out from its mod's
    /// records.</summary>
    private const string WigNotice = "This NPC wears a wig: armor 'HighPolyWig01' in its Default Outfit.";

    private sealed class ListRig
    {
        public Settings Settings = new();
        public VM_NpcSelectionBar Bar = null!;
        public VM_NpcsMenuSelection Row = null!;
        public VM_NpcsMenuSelection OtherRow = null!;

        /// <summary>A mugshot tile without its constructor, given only what the badge code
        /// reads: the settings, the NPC list, the NPC the tile patches, and whether the tile shows
        /// the wig notice. Ends as the constructor does, by reading the stored choice.</summary>
        public VM_NpcsMenuMugshot Tile(FormKey targetNpc, bool hasWigNotice)
        {
            var tile = Reflect.Uninitialized<VM_NpcsMenuMugshot>();
            Reflect.SetField(tile, "_settings", Settings);
            Reflect.SetField(tile, "_vmNpcSelectionBar", Bar);
            Reflect.SetField(tile, "_targetNpcFormKey", targetNpc);
            tile.HasWigNotice = hasWigNotice;
            tile.WigNoticeText = hasWigNotice ? WigNotice : string.Empty;
            tile.ApplyKeptWigHairTint();
            return tile;
        }
    }

    /// <summary>The bar without its constructor, given only what the kept-choice code reads, as
    /// NpcSelectionIndicatorGlueTests builds it.</summary>
    private static ListRig BuildList()
    {
        var rig = new ListRig();

        VM_NpcsMenuSelection Row(FormKey key)
        {
            var row = Reflect.Uninitialized<VM_NpcsMenuSelection>();
            Reflect.SetField(row, "NpcFormKey", key);
            return row;
        }

        rig.Row = Row(Npc);
        rig.OtherRow = Row(OtherNpc);

        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        Reflect.SetField(bar, "_settings", rig.Settings);
        Reflect.SetField(bar, "AllNpcs", new List<VM_NpcsMenuSelection> { rig.Row, rig.OtherRow });
        Reflect.SetField(bar, "_npcVmLookup",
            new Dictionary<FormKey, VM_NpcsMenuSelection> { [Npc] = rig.Row, [OtherNpc] = rig.OtherRow });
        rig.Bar = bar;
        return rig;
    }

    [Theory]
    [InlineData(WigHairTintMode.Always, "Wig hair color kept for this NPC: On")]
    [InlineData(WigHairTintMode.Never, "Wig hair color kept for this NPC: Off")]
    [InlineData(WigHairTintMode.Auto, "Wig hair color kept for this NPC: Auto")]
    public async Task TileBadge_AppearsWithTheStoredChoice_OnlyOnATileWithTheWigNotice_AndItsTooltipNamesTheMode(
        WigHairTintMode kept, string keptLine)
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            var wigTile = rig.Tile(Npc, hasWigNotice: true);
            var tileWithoutWig = rig.Tile(Npc, hasWigNotice: false);
            var otherNpcTile = rig.Tile(OtherNpc, hasWigNotice: true);

            wigTile.HasKeptWigHairTint.Should().BeFalse();
            wigTile.ShowKeptWigHairTintBadge.Should().BeFalse("no choice kept: the plain wig icon");
            wigTile.WigBadgeTooltip.Should().Be(WigNotice, "the wig icon's own tooltip, nothing added");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc }, kept);
            foreach (var tile in new[] { wigTile, tileWithoutWig, otherNpcTile }) tile.ApplyKeptWigHairTint();

            wigTile.ShowKeptWigHairTintBadge.Should().BeTrue(
                "the tile shows the wig notice and the NPC it patches has a kept choice");
            wigTile.KeptWigHairTintTooltip.Should().Be(keptLine);
            wigTile.WigBadgeTooltip.Should().Be(WigNotice + "\n\n" + keptLine,
                "the wig icon keeps its tooltip and gains a line naming the mode");

            tileWithoutWig.HasKeptWigHairTint.Should().BeTrue("the same NPC: its menu offers the clear item");
            tileWithoutWig.ShowKeptWigHairTintBadge.Should().BeFalse("no wig icon on this tile, so no badge either");

            otherNpcTile.HasKeptWigHairTint.Should().BeFalse("only the NPC the choice was kept for");
            otherNpcTile.ShowKeptWigHairTintBadge.Should().BeFalse();
            otherNpcTile.WigBadgeTooltip.Should().Be(WigNotice);
        });
    }

    [Fact]
    public async Task TileBadge_GivesWayToTheNoWigIcon_AndTheTooltipKeepsTheLine()
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            rig.Settings.SetNpcWigHairTintMode(new[] { Npc }, WigHairTintMode.Always);
            var tile = rig.Tile(Npc, hasWigNotice: true);
            tile.ShowKeptWigHairTintBadge.Should().BeTrue();

            // The wig will not reach the output: the tile draws the "No Wig" warning icon.
            tile.WigNotPersisted = true;
            tile.ApplyKeptWigHairTint();

            tile.ShowKeptWigHairTintBadge.Should().BeFalse("one wig icon per tile, and the warning is the one drawn");
            tile.HasKeptWigHairTint.Should().BeTrue("the choice is still there to clear");
            tile.WigBadgeTooltip.Should().Be(WigNotice + "\n\nWig hair color kept for this NPC: On");
        });
    }

    [Fact]
    public async Task TileBadge_FollowsKeepChangeAndRemoval_OnEveryShownTileOfTheNpc_WithoutTheTilesBeingRebuilt()
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            var first = rig.Tile(Npc, hasWigNotice: true);
            var second = rig.Tile(Npc, hasWigNotice: true);
            var otherNpcTile = rig.Tile(OtherNpc, hasWigNotice: true);
            using var firstListens = first.ListenForKeptWigHairTint();
            using var secondListens = second.ListenForKeptWigHairTint();
            using var otherListens = otherNpcTile.ListenForKeptWigHairTint();
            var shown = new[] { first, second };

            // The 3D preview of that NPC, driven as its controls drive it. Nothing below touches
            // the tiles: they follow the preview's announcements.
            using var preview = new VM_WigHairTintPreview(rig.Settings, () => RaceMenuFound);
            preview.Load(ModWithWig(), Npc, eligible: true, Hair, Hair);

            // (Plain values are compared, not the tiles: a failure then prints two words, not
            // every property of two tile view models.)
            preview.Mode = WigHairTintMode.Always;
            shown.Select(t => t.ShowKeptWigHairTintBadge).Should().Equal(new[] { false, false },
                "a flip without Keep stores nothing");

            preview.Keep = true;
            shown.Select(t => t.ShowKeptWigHairTintBadge).Should().Equal(new[] { true, true },
                "kept: every shown tile of the NPC shows it");
            string keptOn = WigNotice + "\n\nWig hair color kept for this NPC: On";
            shown.Select(t => t.WigBadgeTooltip).Should().Equal(keptOn, keptOn);

            preview.Mode = WigHairTintMode.Never;
            shown.Select(t => t.ShowKeptWigHairTintBadge).Should().Equal(new[] { true, true });
            string keptOff = WigNotice + "\n\nWig hair color kept for this NPC: Off";
            shown.Select(t => t.WigBadgeTooltip).Should().Equal(new[] { keptOff, keptOff },
                "a flip while kept changes the mode the tooltip names");

            preview.Keep = false;
            shown.Select(t => t.ShowKeptWigHairTintBadge).Should().Equal(new[] { false, false },
                "the badge goes when the choice does");
            shown.Select(t => t.HasKeptWigHairTint).Should().Equal(false, false);
            shown.Select(t => t.KeptWigHairTintTooltip).Should().Equal(string.Empty, string.Empty);
            shown.Select(t => t.WigBadgeTooltip).Should().Equal(new[] { WigNotice, WigNotice },
                "the plain wig icon's tooltip again");

            otherNpcTile.HasKeptWigHairTint.Should().BeFalse("another NPC's tile never changed");

            // A tile that is no longer shown (disposed) has stopped following.
            firstListens.Dispose();
            preview.Keep = true;
            first.ShowKeptWigHairTintBadge.Should().BeFalse();
            second.ShowKeptWigHairTintBadge.Should().BeTrue();
        });
    }

    [Fact]
    public async Task TileClearMenuItem_ShowsOnlyWithAStoredChoice_AndRemovesIt()
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            var tile = rig.Tile(Npc, hasWigNotice: true);
            var sibling = rig.Tile(Npc, hasWigNotice: false);
            var otherNpcTile = rig.Tile(OtherNpc, hasWigNotice: true);
            using var siblingListens = sibling.ListenForKeptWigHairTint();
            using var otherListens = otherNpcTile.ListenForKeptWigHairTint();
            var announced = new List<FormKey>();
            using var listener = MessageBus.Current.Listen<NpcWigHairTintKeptChangedMessage>()
                .Subscribe(m => announced.Add(m.NpcFormKey));

            // The menu item's visibility is the tile's HasKeptWigHairTint.
            tile.HasKeptWigHairTint.Should().BeFalse("no stored choice: the menu item is hidden");
            tile.ClearKeptWigHairTint();
            announced.Should().BeEmpty("clearing nothing announces nothing");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc, OtherNpc }, WigHairTintMode.Always);
            foreach (var each in new[] { tile, sibling, otherNpcTile }) each.ApplyKeptWigHairTint();
            rig.Bar.ApplyKeptWigHairTint(Npc);
            tile.HasKeptWigHairTint.Should().BeTrue("a stored choice: the menu item is shown");
            tile.KeptWigHairTintTooltip.Should().Be("Wig hair color kept for this NPC: On", "the item's tooltip");
            sibling.HasKeptWigHairTint.Should().BeTrue("on every tile of that NPC, with a wig icon or without");
            rig.Row.HasKeptWigHairTint.Should().BeTrue();

            tile.ClearKeptWigHairTint();

            rig.Settings.GetNpcWigHairTintMode(Npc).Should().BeNull("the kept choice is removed");
            tile.HasKeptWigHairTint.Should().BeFalse("and with it the menu item");
            tile.ShowKeptWigHairTintBadge.Should().BeFalse("and the badge");
            tile.WigBadgeTooltip.Should().Be(WigNotice);
            sibling.HasKeptWigHairTint.Should().BeFalse("the NPC's other shown tiles follow");
            rig.Row.HasKeptWigHairTint.Should().BeFalse("and so does the NPC row's own clear item");
            rig.Settings.GetNpcWigHairTintMode(OtherNpc).Should().Be(WigHairTintMode.Always, "other NPCs keep theirs");
            otherNpcTile.ShowKeptWigHairTintBadge.Should().BeTrue();
            announced.Should().Equal(new[] { Npc }, "an open 3D preview of that NPC is told");
        });
    }

    [Fact]
    public async Task Row_FollowsTheStoredChoice_ForItsClearMenuItem_WithoutTheListBeingRebuilt()
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            rig.Row.HasKeptWigHairTint.Should().BeFalse("no choice kept: no clear item");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc }, WigHairTintMode.Always);
            rig.Bar.ApplyKeptWigHairTint(Npc);
            var rowBefore = rig.Row;
            rig.Row.HasKeptWigHairTint.Should().BeTrue();
            rig.Row.KeptWigHairTintTooltip.Should().Be("Wig hair color kept for this NPC: On");
            rig.OtherRow.HasKeptWigHairTint.Should().BeFalse("only the NPC the choice was kept for");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc }, WigHairTintMode.Never);
            rig.Bar.ApplyKeptWigHairTint(Npc);
            rig.Row.KeptWigHairTintTooltip.Should().Be("Wig hair color kept for this NPC: Off");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc }, null);
            rig.Bar.ApplyKeptWigHairTint(Npc);
            rig.Row.HasKeptWigHairTint.Should().BeFalse("the clear item goes when the choice does");
            rig.Row.KeptWigHairTintTooltip.Should().BeEmpty();
            rig.Row.Should().BeSameAs(rowBefore, "the same row object was updated in place");

            // An NPC that has no row is nothing to update.
            rig.Bar.Invoking(b => b.ApplyKeptWigHairTint(MutagenFixtures.Fk("000007:Skyrim.esm"))).Should().NotThrow();
        });
    }

    [Fact]
    public async Task RowClearMenuItem_ShowsOnlyWithAStoredChoice_AndRemovesIt_AndTheTilesFollow()
    {
        await OnSta(() =>
        {
            var rig = BuildList();
            var tile = rig.Tile(Npc, hasWigNotice: true);
            using var tileListens = tile.ListenForKeptWigHairTint();
            var announced = new List<FormKey>();
            using var listener = MessageBus.Current.Listen<NpcWigHairTintKeptChangedMessage>()
                .Subscribe(m => announced.Add(m.NpcFormKey));

            // The menu item's visibility is the row's HasKeptWigHairTint.
            rig.Row.HasKeptWigHairTint.Should().BeFalse("no stored choice: the menu item is hidden");
            rig.Bar.ClearKeptWigHairTint(rig.Row);
            announced.Should().BeEmpty("clearing nothing announces nothing");

            rig.Settings.SetNpcWigHairTintMode(new[] { Npc, OtherNpc }, WigHairTintMode.Always);
            rig.Bar.ApplyKeptWigHairTint(Npc);
            rig.Bar.ApplyKeptWigHairTint(OtherNpc);
            tile.ApplyKeptWigHairTint();
            rig.Row.HasKeptWigHairTint.Should().BeTrue("a stored choice: the menu item is shown");
            tile.ShowKeptWigHairTintBadge.Should().BeTrue();

            rig.Bar.ClearKeptWigHairTint(rig.Row);

            rig.Settings.GetNpcWigHairTintMode(Npc).Should().BeNull("the kept choice is removed");
            rig.Row.HasKeptWigHairTint.Should().BeFalse("and with it the menu item");
            tile.ShowKeptWigHairTintBadge.Should().BeFalse("and the badge on the NPC's shown tiles");
            rig.Settings.GetNpcWigHairTintMode(OtherNpc).Should().Be(WigHairTintMode.Always, "other NPCs keep theirs");
            rig.OtherRow.HasKeptWigHairTint.Should().BeTrue();
            announced.Should().Equal(new[] { Npc }, "an open 3D preview of that NPC is told");
        });
    }
}
