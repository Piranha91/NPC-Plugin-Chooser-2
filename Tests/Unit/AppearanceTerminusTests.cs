using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Auxilliary.TryResolveAppearanceTerminus"/> — separates the three outcomes of walking
/// an NPC's Traits template chain, which the FaceGen ladder needs because only one of them
/// (a genuinely broken chain) should stop a patch.
///
/// <para>The walk starts from the donor RECORD rather than re-resolving its FormKey, and that is
/// the subtle part: the record the user selected and the load order's winning override can
/// disagree about whether an NPC inherits at all. A first measurement pass misreported 18 healthy
/// NPCs as unfollowable for exactly that reason.</para>
/// </summary>
public class AppearanceTerminusTests
{
    /// <summary>Resolver over an explicit record set — nothing else resolves, which is how a
    /// dangling link is modelled.</summary>
    private static Func<FormKey, INpcGetter?> Resolver(params INpcGetter[] known)
    {
        var map = known.ToDictionary(n => n.FormKey, n => n);
        return fk => map.TryGetValue(fk, out var n) ? n : null;
    }

    [Fact]
    public void UntemplatedDonor_IsNotTemplated_AndTerminusIsItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Plain");

        var status = Auxilliary.TryResolveAppearanceTerminus(npc, Resolver(npc), out var terminus);

        status.Should().Be(FaceGenChainStatus.NotTemplated);
        terminus.Should().Be(npc.FormKey);
    }

    [Fact]
    public void OneHopToAnUntemplatedRecord_Resolves()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var target = MutagenFixtures.NewNpc(mod, "TreasCorpseCWImperialMale");
        var donor = MutagenFixtures.NewNpc(mod, "Herebane", traitsTemplate: true, template: target);

        var status = Auxilliary.TryResolveAppearanceTerminus(donor, Resolver(donor, target), out var terminus);

        status.Should().Be(FaceGenChainStatus.Resolved);
        terminus.Should().Be(target.FormKey);
    }

    [Fact]
    public void MultiHopChain_ResolvesToTheLastRecord()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var last = MutagenFixtures.NewNpc(mod, "Last");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: last);
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: middle);

        var status = Auxilliary.TryResolveAppearanceTerminus(
            donor, Resolver(donor, middle, last), out var terminus);

        status.Should().Be(FaceGenChainStatus.Resolved);
        terminus.Should().Be(last.FormKey);
    }

    [Fact]
    public void WalkStartsFromTheDonorRecord_NotTheResolvedWinner()
    {
        // The regression this class exists for. The resolver stands in for the load order, whose
        // version of the donor does NOT inherit; the selected record does. Re-resolving the donor's
        // FormKey would start the walk at the non-inheriting winner, terminate immediately, and
        // report the chain unfollowable — even though it is one clean hop.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var target = MutagenFixtures.NewNpc(mod, "Target");
        var donorAsSelected = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: target);

        // Stands in for the load order's winning override of the donor: same NPC, but it does not
        // inherit. Resolving the donor's FormKey yields THIS, not the record the user selected.
        var winningVersion = MutagenFixtures.NewNpc(MutagenFixtures.NewMod("Winner.esp"), "DonorAsWinner");

        var map = new Dictionary<FormKey, INpcGetter>
        {
            [donorAsSelected.FormKey] = winningVersion,
            [target.FormKey] = target,
        };

        var status = Auxilliary.TryResolveAppearanceTerminus(
            donorAsSelected, fk => map.TryGetValue(fk, out var n) ? n : null, out var terminus);

        status.Should().Be(FaceGenChainStatus.Resolved);
        terminus.Should().Be(target.FormKey);
    }

    [Fact]
    public void DanglingLink_IsUnfollowable()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var missing = MutagenFixtures.NewNpc(mod, "Missing");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: missing);

        // 'missing' is deliberately absent from the resolver.
        var status = Auxilliary.TryResolveAppearanceTerminus(donor, Resolver(donor), out var terminus);

        status.Should().Be(FaceGenChainStatus.Unfollowable);
        terminus.Should().Be(donor.FormKey);
    }

    [Fact]
    public void LeveledLinkIsRecognised_AndIsNotAFailure()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var listStandIn = MutagenFixtures.NewNpc(mod, "PretendLeveledList");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: listStandIn);

        var status = Auxilliary.TryResolveAppearanceTerminus(
            donor, Resolver(donor), out var terminus,
            isLeveledNpc: fk => fk.Equals(listStandIn.FormKey));

        status.Should().Be(FaceGenChainStatus.LeveledTerminus);
        terminus.Should().Be(donor.FormKey);
    }

    [Fact]
    public void Cycle_IsUnfollowable()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var a = MutagenFixtures.NewNpc(mod, "A");
        var b = MutagenFixtures.NewNpc(mod, "B", traitsTemplate: true, template: a);
        a.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Traits;
        a.Template.SetTo(b.FormKey);

        var status = Auxilliary.TryResolveAppearanceTerminus(b, Resolver(a, b), out var terminus);

        status.Should().Be(FaceGenChainStatus.Unfollowable);
        terminus.Should().Be(b.FormKey);
    }

    [Fact]
    public void Trace_RecordsEachHopAndTheOutcome()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var target = MutagenFixtures.NewNpc(mod, "Target");
        var donor = MutagenFixtures.NewNpc(mod, "Donor", traitsTemplate: true, template: target);

        var hops = new List<string>();
        Auxilliary.TryResolveAppearanceTerminus(
            donor, Resolver(donor, target), out _, trace: hops.Add);

        hops.Should().HaveCount(2);
        hops[0].Should().Contain(target.FormKey.ToString());
        hops[1].Should().StartWith("terminus");
    }
}
