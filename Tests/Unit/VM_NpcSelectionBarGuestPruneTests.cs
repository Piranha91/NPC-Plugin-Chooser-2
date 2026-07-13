using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="VM_NpcSelectionBar.PruneStaleGuestAppearances"/> — the reconciliation sweep
/// that removes persisted guest/shared appearances whose donor NPC a mod no longer contains
/// (and, with empty live/fresh sets, every share sourced from a removed mod entry). Pure
/// function of <see cref="Settings"/> state, so the VM is allocated with
/// <see cref="Reflect.Uninitialized{T}"/> and only the two fields the sweep path reads
/// (<c>_settings</c>, <c>_consistencyProvider</c>) are poked in; the
/// <see cref="NpcConsistencyProvider"/> is real so selection-clearing is exercised
/// end-to-end. No STA / scheduler / game install required.
/// </summary>
public class VM_NpcSelectionBarGuestPruneTests
{
    private static readonly FormKey TargetA = MutagenFixtures.Fk("000801:Skyrim.esm");
    private static readonly FormKey TargetB = MutagenFixtures.Fk("000802:Skyrim.esm");
    private static readonly FormKey DonorLive = MutagenFixtures.Fk("000D01:Chooey.esp");
    private static readonly FormKey DonorGone = MutagenFixtures.Fk("000D02:Chooey.esp");
    private static readonly FormKey DonorEnv = MutagenFixtures.Fk("000D03:Foundation.esp");

    private const string Mod = "Chooey's Replacer";
    private const string OtherMod = "Other Mod";

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

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

    private static HashSet<FormKey> Set(params FormKey[] keys) => keys.ToHashSet();

    // ------------------------------------------------------------------
    // Staleness decision
    // ------------------------------------------------------------------

    [Fact]
    public void StaleDonor_ShareRemoved_AndEmptyTargetEntryDropped()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        removed.Should().Be(1);
        s.GuestAppearances.Should().NotContainKey(TargetA,
            "the last share for the target was pruned, so the whole entry should go");
    }

    [Fact]
    public void LiveDonor_ShareKept()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorLive, "Chooey");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        removed.Should().Be(0);
        s.GuestAppearances[TargetA].Should().ContainSingle()
            .Which.Should().Be((Mod, DonorLive, "Chooey"));
    }

    [Fact]
    public void FreshIniDonor_ShareKept_EvenWhenNotAmongModOwnNpcs()
    {
        // An ini donor can resolve via the load order (environment editor-ID map) without
        // being one of the mod's own NPC records — the fresh set must protect it.
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorEnv, "Amalee");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set(DonorEnv));

        removed.Should().Be(0);
        s.GuestAppearances[TargetA].Should().Contain((Mod, DonorEnv, "Amalee"));
    }

    [Fact]
    public void OtherModsShares_Untouched_EvenForSameDonorKey()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        AddGuest(s, TargetB, OtherMod, DonorGone, "Chooey");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        removed.Should().Be(1);
        s.GuestAppearances.Should().NotContainKey(TargetA);
        s.GuestAppearances[TargetB].Should().ContainSingle()
            .Which.Should().Be((OtherMod, DonorGone, "Chooey"));
    }

    [Fact]
    public void ModNameMatch_IsCaseInsensitive()
    {
        var s = new Settings();
        AddGuest(s, TargetA, "CHOOEY'S REPLACER", DonorGone, "Chooey");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        removed.Should().Be(1);
        s.GuestAppearances.Should().NotContainKey(TargetA);
    }

    [Fact]
    public void SameStaleDonor_SharedToMultipleTargets_AllRemoved()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        AddGuest(s, TargetB, Mod, DonorGone, "Chooey");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        removed.Should().Be(2);
        s.GuestAppearances.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // Mod-removal mode (empty live + fresh sets)
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyLiveAndFreshSets_SweepEveryShareFromThatMod()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorLive, "Chooey");
        AddGuest(s, TargetB, Mod, DonorGone, "Chooey");
        AddGuest(s, TargetB, OtherMod, DonorEnv, "Amalee");
        var bar = MakeBar(s);

        var removed = bar.PruneStaleGuestAppearances(Mod, Set(), Set());

        removed.Should().Be(2);
        s.GuestAppearances.Should().NotContainKey(TargetA);
        s.GuestAppearances[TargetB].Should().ContainSingle()
            .Which.Should().Be((OtherMod, DonorEnv, "Amalee"));
    }

    // ------------------------------------------------------------------
    // Side-effect bookkeeping (selection, randomized subset, template flags)
    // ------------------------------------------------------------------

    [Fact]
    public void SelectionPointingAtPrunedShare_IsCleared_OthersUntouched()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        s.SelectedAppearanceMods[TargetA] = (Mod, DonorGone); // guest selection about to go stale
        s.SelectedAppearanceMods[TargetB] = (Mod, TargetB);   // own-face selection, same mod
        var bar = MakeBar(s);

        bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        s.SelectedAppearanceMods.Should().NotContainKey(TargetA,
            "a selection referencing the pruned share would dangle");
        s.SelectedAppearanceMods.Should().ContainKey(TargetB);
    }

    [Fact]
    public void RandomizedShareSubset_StaysInSync()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        s.RandomizedGuestAppearances[TargetA] =
            new HashSet<(string, FormKey, string)> { (Mod, DonorGone, "Chooey") };
        var bar = MakeBar(s);

        bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        s.RandomizedGuestAppearances.Should().NotContainKey(TargetA);
    }

    [Fact]
    public void TemplateFlag_DroppedWhenLastShareReferencingDonorIsPruned()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        s.CachedSkyPatcherTemplates.Add(DonorGone);
        var bar = MakeBar(s);

        bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        s.CachedSkyPatcherTemplates.Should().NotContain(DonorGone,
            "an unreferenced flag would hide the FormKey from the NPC list forever");
    }

    [Fact]
    public void TemplateFlag_KeptWhileAnotherModsShareStillReferencesDonor()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorGone, "Chooey");
        AddGuest(s, TargetB, OtherMod, DonorGone, "Chooey");
        s.CachedSkyPatcherTemplates.Add(DonorGone);
        var bar = MakeBar(s);

        bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        s.CachedSkyPatcherTemplates.Should().Contain(DonorGone);
    }

    [Fact]
    public void TemplateFlag_OfDonorWithNoPrunedShare_Untouched()
    {
        var s = new Settings();
        AddGuest(s, TargetA, Mod, DonorLive, "Chooey");
        s.CachedSkyPatcherTemplates.Add(DonorLive);
        s.CachedSkyPatcherTemplates.Add(DonorEnv); // no share references this one at all
        var bar = MakeBar(s);

        bar.PruneStaleGuestAppearances(Mod, Set(DonorLive), Set());

        s.CachedSkyPatcherTemplates.Should().Contain(DonorLive);
        s.CachedSkyPatcherTemplates.Should().Contain(DonorEnv,
            "the sweep only reconsiders flags whose shares it just removed");
    }
}
