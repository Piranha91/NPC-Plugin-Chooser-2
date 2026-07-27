using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="VM_NpcSelectionBar.AddGuestAppearance"/> — the single funnel every share path
/// (share dialog, favorites, profile import, randomizer) goes through. Its job here is the
/// self-share guard: an NPC shared with ITSELF produces a guest entry indistinguishable from
/// the NPC's own native appearance, so the resulting tile is built with
/// <c>IsGuestAppearance == false</c>, never offers "Unshare from this NPC", and the entry can
/// never be removed. The VM is allocated with <see cref="Reflect.Uninitialized{T}"/> with only
/// the fields this path reads poked in (<c>_settings</c>, <c>_consistencyProvider</c>);
/// <c>SelectedNpc</c> stays null so no view refresh is attempted. No STA / game install needed.
/// </summary>
public class VM_NpcSelectionBarSelfShareTests
{
    private static readonly FormKey Npc = MutagenFixtures.Fk("000801:Skyrim.esm");
    private static readonly FormKey OtherNpc = MutagenFixtures.Fk("000802:Skyrim.esm");

    private const string Mod = "Chooey's Replacer";

    private static VM_NpcSelectionBar MakeBar(Settings settings)
    {
        var bar = Reflect.Uninitialized<VM_NpcSelectionBar>();
        Reflect.SetField(bar, "_settings", settings);
        Reflect.SetField(bar, "_consistencyProvider",
            new NpcConsistencyProvider(settings, new Lazy<VM_Settings>(() => null!)));
        return bar;
    }

    [Fact]
    public void SelfShare_IsNotPersisted()
    {
        var s = new Settings();
        var bar = MakeBar(s);

        bar.AddGuestAppearance(Npc, Mod, Npc, "Lydia");

        s.GuestAppearances.Should().BeEmpty(
            "an NPC shared with itself yields an entry that can never be unshared");
    }

    [Fact]
    public void SelfShare_DoesNotCreateAnEmptyTargetEntry()
    {
        var s = new Settings();
        var bar = MakeBar(s);

        bar.AddGuestAppearance(Npc, Mod, Npc, "Lydia");

        // The guard returns before the target's set is created, so no stray empty key is left
        // behind for later readers to iterate.
        s.GuestAppearances.Should().NotContainKey(Npc);
    }

    [Fact]
    public void SelfShare_LeavesExistingSharesForTheSameNpcIntact()
    {
        var s = new Settings();
        var bar = MakeBar(s);
        bar.AddGuestAppearance(Npc, Mod, OtherNpc, "Amalee");

        bar.AddGuestAppearance(Npc, Mod, Npc, "Lydia");

        s.GuestAppearances[Npc].Should().ContainSingle()
            .Which.Should().Be((Mod, OtherNpc, "Amalee"));
    }

    [Fact]
    public void CrossNpcShare_IsStillPersisted()
    {
        var s = new Settings();
        var bar = MakeBar(s);

        bar.AddGuestAppearance(Npc, Mod, OtherNpc, "Amalee");

        s.GuestAppearances[Npc].Should().ContainSingle()
            .Which.Should().Be((Mod, OtherNpc, "Amalee"));
    }
}
