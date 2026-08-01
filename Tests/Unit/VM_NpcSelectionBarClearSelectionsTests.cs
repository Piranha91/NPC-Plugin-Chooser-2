using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="VM_NpcSelectionBar.ClearSelectionsFromMod"/> and
/// <see cref="VM_NpcSelectionBar.CountNpcStateFromMod"/> — the deselection half of what happens
/// when a mod entry is deleted in the Mods menu (the share half is
/// <see cref="VM_NpcSelectionBar.PruneStaleGuestAppearances"/>, covered by
/// <see cref="VM_NpcSelectionBarGuestPruneTests"/>). Same harness: the VM is allocated with
/// <see cref="Reflect.Uninitialized{T}"/> and only <c>_settings</c> / <c>_consistencyProvider</c>
/// are poked in, with a real <see cref="NpcConsistencyProvider"/> so the clearing runs end-to-end.
/// </summary>
public class VM_NpcSelectionBarClearSelectionsTests
{
    private static readonly FormKey TargetA = MutagenFixtures.Fk("000801:Skyrim.esm");
    private static readonly FormKey TargetB = MutagenFixtures.Fk("000802:Skyrim.esm");
    private static readonly FormKey TargetC = MutagenFixtures.Fk("000803:Skyrim.esm");
    private static readonly FormKey Donor = MutagenFixtures.Fk("000D01:Chooey.esp");

    private const string Mod = "Chooey's Replacer";
    private const string OtherMod = "Other Mod";

    /// <summary>Builds the bar AFTER settings are seeded: the consistency provider snapshots
    /// <see cref="Settings.SelectedAppearanceMods"/> into its cache at construction.</summary>
    private static VM_NpcSelectionBar MakeBar(Settings settings)
    {
        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        Reflect.SetField(bar, "_settings", settings);
        Reflect.SetField(bar, "_consistencyProvider",
            new NpcConsistencyProvider(settings, new Lazy<VM_Settings>(() => null!)));
        return bar;
    }

    private static void AddGuest(Settings s, FormKey target, string modName, FormKey donor, string display)
    {
        if (!s.GuestAppearances.TryGetValue(target, out var set))
        {
            set = new HashSet<(string, FormKey, string)>();
            s.GuestAppearances[target] = set;
        }
        set.Add((modName, donor, display));
    }

    // ------------------------------------------------------------------
    // ClearSelectionsFromMod
    // ------------------------------------------------------------------

    [Fact]
    public void OwnFaceSelections_FromThatMod_AreCleared()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        s.SelectedAppearanceMods[TargetB] = (Mod, TargetB);
        var bar = MakeBar(s);

        var cleared = bar.ClearSelectionsFromMod(Mod);

        cleared.Should().Be(2);
        s.SelectedAppearanceMods.Should().BeEmpty();
    }

    [Fact]
    public void SharedSelections_SourcedFromThatMod_AreCleared()
    {
        // A selection whose source NPC is a donor from the deleted mod: dangles just as badly
        // as an own-face pick, and is swept by the same mod-name match.
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, Donor);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod).Should().Be(1);
        s.SelectedAppearanceMods.Should().NotContainKey(TargetA);
    }

    [Fact]
    public void OtherModsSelections_AreUntouched()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        s.SelectedAppearanceMods[TargetB] = (OtherMod, TargetB);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod).Should().Be(1);
        s.SelectedAppearanceMods.Should().ContainKey(TargetB)
            .WhoseValue.Should().Be((OtherMod, TargetB));
    }

    [Fact]
    public void ModNameMatch_IsCaseInsensitive()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = ("CHOOEY'S REPLACER", TargetA);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod).Should().Be(1);
        s.SelectedAppearanceMods.Should().BeEmpty();
    }

    [Fact]
    public void ConsistencyProviderCache_StaysInSync_NotJustTheSettingsDictionary()
    {
        // The provider serves the NPC menu from its own cache; a selection cleared only in
        // Settings would still read back as chosen for the rest of the session.
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        var bar = MakeBar(s);
        var provider = Reflect.GetField<NpcConsistencyProvider>(bar, "_consistencyProvider");

        bar.ClearSelectionsFromMod(Mod);

        provider.GetSelectedMod(TargetA).ModName.Should().BeNull();
    }

    [Fact]
    public void RandomizerRecord_OfClearedSelection_IsDropped()
    {
        // Left behind, it would match again if the user later re-added the mod and picked it
        // manually -- "Clear Randomized NPCs" would then wipe a hand-made selection.
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        s.RandomizedSelections[TargetA] = (Mod, TargetA);
        s.RandomizedSelections[TargetB] = (OtherMod, TargetB);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod);

        s.RandomizedSelections.Should().NotContainKey(TargetA);
        s.RandomizedSelections.Should().ContainKey(TargetB);
    }

    [Fact]
    public void NoSelectionsFromThatMod_IsANoOp()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (OtherMod, TargetA);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod).Should().Be(0);
        s.SelectedAppearanceMods.Should().ContainKey(TargetA);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankModName_ClearsNothing(string? modName)
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(modName!).Should().Be(0);
        s.SelectedAppearanceMods.Should().ContainKey(TargetA);
    }

    // ------------------------------------------------------------------
    // Source-filtered mode: the entry survives, but stopped providing some faces
    // ------------------------------------------------------------------

    [Fact]
    public void SourceFilter_ClearsOnlySelectionsOfTheFacesThatWentAway()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA); // face the mod no longer provides
        s.SelectedAppearanceMods[TargetB] = (Mod, TargetB); // face the mod still provides
        var bar = MakeBar(s);

        var cleared = bar.ClearSelectionsFromMod(Mod, new HashSet<FormKey> { TargetA });

        cleared.Should().Be(1);
        s.SelectedAppearanceMods.Should().NotContainKey(TargetA);
        s.SelectedAppearanceMods.Should().ContainKey(TargetB);
    }

    [Fact]
    public void SourceFilter_KeepsAStillValidShareOnAnNpcTheModStoppedProviding()
    {
        // TargetA dropped out of the mod, but its selection is a SHARE of a donor the mod still
        // has — a share never required the mod to provide the target, so it stays.
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, Donor);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod, new HashSet<FormKey> { TargetA }).Should().Be(0);
        s.SelectedAppearanceMods.Should().ContainKey(TargetA);
    }

    [Fact]
    public void SourceFilter_ClearsAShareSelectionWhoseDonorWentAway()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, Donor);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod, new HashSet<FormKey> { Donor }).Should().Be(1);
        s.SelectedAppearanceMods.Should().NotContainKey(TargetA);
    }

    [Fact]
    public void SourceFilter_RandomizerRecord_FollowsTheSameSourceMatch()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        s.RandomizedSelections[TargetA] = (Mod, TargetA);
        s.RandomizedSelections[TargetB] = (Mod, TargetB); // face still provided
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod, new HashSet<FormKey> { TargetA });

        s.RandomizedSelections.Should().NotContainKey(TargetA);
        s.RandomizedSelections.Should().ContainKey(TargetB);
    }

    [Fact]
    public void SourceFilter_EmptySet_ClearsNothing()
    {
        // Nothing went away — must not be read as "no face survives".
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        var bar = MakeBar(s);

        bar.ClearSelectionsFromMod(Mod, new HashSet<FormKey>()).Should().Be(0);
        s.SelectedAppearanceMods.Should().ContainKey(TargetA);
    }

    // ------------------------------------------------------------------
    // CountNpcStateFromMod (delete-confirmation preview)
    // ------------------------------------------------------------------

    [Fact]
    public void Count_ReportsSelectionsAndShares_ForThatModOnly()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        s.SelectedAppearanceMods[TargetB] = (OtherMod, TargetB);
        AddGuest(s, TargetB, Mod, Donor, "Chooey");
        AddGuest(s, TargetC, Mod, Donor, "Chooey");
        AddGuest(s, TargetC, OtherMod, Donor, "Chooey");
        var bar = MakeBar(s);

        bar.CountNpcStateFromMod(Mod).Should().Be((1, 2));
    }

    [Fact]
    public void Count_IsReadOnly()
    {
        var s = new Settings();
        s.SelectedAppearanceMods[TargetA] = (Mod, TargetA);
        AddGuest(s, TargetB, Mod, Donor, "Chooey");
        var bar = MakeBar(s);

        bar.CountNpcStateFromMod(Mod);

        s.SelectedAppearanceMods.Should().ContainKey(TargetA);
        s.GuestAppearances.Should().ContainKey(TargetB);
    }
}
