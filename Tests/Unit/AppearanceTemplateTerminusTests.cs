using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="Auxilliary.ResolveAppearanceTemplateTerminus"/> — the Traits-template walk that
/// tells the mugshot/preview pipeline whose face to draw for an NPC that inherits its traits.
///
/// A Traits-templated NPC ships no FaceGen of its own, so before this existed the renderer
/// declined it and the tile kept the placeholder (the reported symptom: Redguard Woman showing
/// the placeholder under Base Game / High Poly NPC Overhaul, both of which template her onto
/// TreasCorpseCommonerRedguardFemale).
///
/// The function takes its record resolver as a delegate, so these tests drive it with an
/// in-memory FormKey→record map: no environment, no link cache, no game install.
/// </summary>
public class AppearanceTemplateTerminusTests
{
    /// <summary>Resolver over an explicit record set; anything absent resolves to null, which is
    /// how a dangling link or a non-NPC (Leveled NPC) target reaches the walk.</summary>
    private static Func<FormKey, INpcGetter?> Resolver(params INpcGetter[] npcs)
    {
        var map = new Dictionary<FormKey, INpcGetter>();
        foreach (var npc in npcs) map[npc.FormKey] = npc;
        return fk => map.TryGetValue(fk, out var rec) ? rec : null;
    }

    private static FormKey Walk(FormKey start, Func<FormKey, INpcGetter?> resolve, int maxDepth = 50) =>
        Auxilliary.ResolveAppearanceTemplateTerminus(start, resolve, maxDepth);

    // ---- the substituting cases -------------------------------------------------------------

    [Fact]
    public void SingleHop_ReturnsTheTemplate()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "TreasCorpseCommonerRedguardFemale");
        var npc = MutagenFixtures.NewNpc(mod, "RedguardWoman", traitsTemplate: true, template: template);

        Walk(npc.FormKey, Resolver(npc, template)).Should().Be(template.FormKey);
    }

    [Fact]
    public void MultiHop_ReturnsTheEndOfTheChain()
    {
        // Only the LAST record in the chain owns an appearance; intermediate hops inherit too.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var root = MutagenFixtures.NewNpc(mod, "Root");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: root);
        var npc = MutagenFixtures.NewNpc(mod, "Leaf", traitsTemplate: true, template: middle);

        Walk(npc.FormKey, Resolver(npc, middle, root)).Should().Be(root.FormKey);
    }

    [Fact]
    public void IntermediateHopWithItsOwnFaceGenIsStillWalkedThrough()
    {
        // A record can carry a Traits flag AND FaceGen files; the game ignores the files and
        // uses the template, so the walk must not stop early on such a record.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var root = MutagenFixtures.NewNpc(mod, "Root");
        var middle = MutagenFixtures.NewNpc(mod, "MiddleWithHeadParts", traitsTemplate: true, template: root);
        middle.HeadParts.Add(mod.HeadParts.AddNew());
        var npc = MutagenFixtures.NewNpc(mod, "Leaf", traitsTemplate: true, template: middle);

        Walk(npc.FormKey, Resolver(npc, middle, root)).Should().Be(root.FormKey);
    }

    // ---- the pass-through cases (caller keeps its existing no-FaceGen behaviour) -------------

    [Fact]
    public void NotTemplated_ReturnsTheNpcItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "Plain");

        Walk(npc.FormKey, Resolver(npc)).Should().Be(npc.FormKey);
    }

    [Fact]
    public void TemplateLinkWithoutTheTraitsFlag_IsNotAnAppearanceTemplate()
    {
        // TPLT also drives inventory / AI / faction inheritance. Without the Traits bit the
        // NPC's own face is used, so the link must be ignored here.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var npc = MutagenFixtures.NewNpc(mod, "InventoryOnly", traitsTemplate: false, template: template);

        Walk(npc.FormKey, Resolver(npc, template)).Should().Be(npc.FormKey);
    }

    [Fact]
    public void TraitsFlagWithNoTemplate_ReturnsTheNpcItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "FlagButNoLink", traitsTemplate: true);

        Walk(npc.FormKey, Resolver(npc)).Should().Be(npc.FormKey);
    }

    [Fact]
    public void DanglingTemplateLink_ReturnsTheNpcItself()
    {
        // The template isn't in the resolver's scope (missing master, or a Leveled NPC, which
        // never resolves as an INpcGetter) — there is no fixed appearance to borrow.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var absent = MutagenFixtures.NewNpc(mod, "Absent");
        var npc = MutagenFixtures.NewNpc(mod, "Orphan", traitsTemplate: true, template: absent);

        Walk(npc.FormKey, Resolver(npc)).Should().Be(npc.FormKey);
    }

    [Fact]
    public void UnresolvableStartingNpc_ReturnsTheNpcItself()
    {
        var missing = MutagenFixtures.Fk("000800:Absent.esp");

        Walk(missing, Resolver()).Should().Be(missing);
    }

    [Fact]
    public void SelfTemplate_IsACycleAndReturnsTheNpcItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npc = MutagenFixtures.NewNpc(mod, "SelfReferential", traitsTemplate: true);
        npc.Template.SetTo(npc.FormKey);

        Walk(npc.FormKey, Resolver(npc)).Should().Be(npc.FormKey);
    }

    [Fact]
    public void TwoNodeCycle_ReturnsTheNpcItself()
    {
        // A → B → A. Without the visited set this loops until maxDepth; either way the answer
        // must be "no usable appearance", not an arbitrary member of the cycle.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var a = MutagenFixtures.NewNpc(mod, "A", traitsTemplate: true);
        var b = MutagenFixtures.NewNpc(mod, "B", traitsTemplate: true, template: a);
        a.Template.SetTo(b.FormKey);

        Walk(a.FormKey, Resolver(a, b)).Should().Be(a.FormKey);
    }

    [Fact]
    public void ChainLongerThanMaxDepth_ReturnsTheNpcItself()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var records = new List<Npc> { MutagenFixtures.NewNpc(mod, "Chain0") };
        for (int i = 1; i <= 6; i++)
        {
            records.Add(MutagenFixtures.NewNpc(mod, "Chain" + i, traitsTemplate: true, template: records[i - 1]));
        }
        var start = records[^1];

        // 6 hops to the root, plus one more visit to confirm the root owns its traits.
        Walk(start.FormKey, Resolver(records.ToArray()), maxDepth: 7).Should().Be(records[0].FormKey);
        // One short: the walk gives up rather than returning a partial answer (a mid-chain
        // record's face would be the wrong face).
        Walk(start.FormKey, Resolver(records.ToArray()), maxDepth: 6).Should().Be(start.FormKey);
        Walk(start.FormKey, Resolver(records.ToArray()), maxDepth: 3).Should().Be(start.FormKey);
    }

    // ---- scope sensitivity -------------------------------------------------------------------

    [Fact]
    public void ResolverScopeDecidesTheAnswer_ModsMayRePointTheTemplate()
    {
        // Two mods can legitimately disagree about an NPC's template, which is why the walk
        // takes the resolver rather than reading a single global winner: each mugshot tile
        // resolves through its own mod's records.
        var vanilla = MutagenFixtures.NewMod("Vanilla.esp");
        var vanillaTemplate = MutagenFixtures.NewNpc(vanilla, "VanillaTemplate");
        var vanillaNpc = MutagenFixtures.NewNpc(vanilla, "Npc", traitsTemplate: true, template: vanillaTemplate);

        // Same FormKey, different record content — what an override looks like to a scoped resolver.
        var appearanceMod = MutagenFixtures.NewMod("Appearance.esp");
        var modTemplate = MutagenFixtures.NewNpc(appearanceMod, "ModTemplate");
        var overrideNpc = appearanceMod.Npcs.GetOrAddAsOverride(vanillaNpc);
        overrideNpc.Template.SetTo(modTemplate.FormKey);

        Func<FormKey, INpcGetter?> vanillaScope = fk =>
            fk == vanillaNpc.FormKey ? vanillaNpc
            : fk == vanillaTemplate.FormKey ? vanillaTemplate
            : null;
        Func<FormKey, INpcGetter?> modScope = fk =>
            fk == overrideNpc.FormKey ? overrideNpc
            : fk == modTemplate.FormKey ? modTemplate
            : null;

        Walk(vanillaNpc.FormKey, vanillaScope).Should().Be(vanillaTemplate.FormKey);
        Walk(vanillaNpc.FormKey, modScope).Should().Be(modTemplate.FormKey);
    }

    // ---- Auxilliary.TemplateChainDepth -------------------------------------------------------
    //
    // Randomize orders its run by this depth so a face's owner is decided before anyone who
    // copies that face: a templated NPC's own selection is inert, so it can only be made
    // consistent against a terminus that has already been settled. Everything the walk above
    // treats as "no usable appearance" has to land in band 0 — those NPCs have no in-scope
    // record to be made consistent WITH, so making the rest of the run wait on them would be
    // waiting on nothing.

    private static int Depth(FormKey start, Func<FormKey, INpcGetter?> resolve, int maxDepth = 50) =>
        Auxilliary.TemplateChainDepth(start, resolve, maxDepth);

    [Fact]
    public void Depth_CountsHopsToTheFaceOwner()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var root = MutagenFixtures.NewNpc(mod, "Root");
        var middle = MutagenFixtures.NewNpc(mod, "Middle", traitsTemplate: true, template: root);
        var leaf = MutagenFixtures.NewNpc(mod, "Leaf", traitsTemplate: true, template: middle);
        var resolve = Resolver(leaf, middle, root);

        Depth(root.FormKey, resolve).Should().Be(0, "the root owns its face");
        Depth(middle.FormKey, resolve).Should().Be(1);
        Depth(leaf.FormKey, resolve).Should().Be(2);

        // The property randomize actually relies on: sorting by depth puts every NPC after the
        // one it copies from, whatever order the list arrives in.
        new[] { leaf.FormKey, root.FormKey, middle.FormKey }
            .OrderBy(fk => Depth(fk, resolve))
            .Should().Equal(root.FormKey, middle.FormKey, leaf.FormKey);
    }

    [Fact]
    public void Depth_TemplateLinkWithoutTheTraitsFlag_IsZero()
    {
        // TPLT without the Traits bit inherits inventory/AI, not the face — nothing to wait for.
        var mod = MutagenFixtures.NewMod("Test.esp");
        var template = MutagenFixtures.NewNpc(mod, "Template");
        var npc = MutagenFixtures.NewNpc(mod, "InventoryOnly", traitsTemplate: false, template: template);

        Depth(npc.FormKey, Resolver(npc, template)).Should().Be(0);
    }

    [Fact]
    public void Depth_UnfollowableChains_AreZero()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");

        // Dangling link (missing master, or a Leveled NPC, which never resolves as an INpcGetter).
        var absent = MutagenFixtures.NewNpc(mod, "Absent");
        var orphan = MutagenFixtures.NewNpc(mod, "Orphan", traitsTemplate: true, template: absent);
        Depth(orphan.FormKey, Resolver(orphan)).Should().Be(0);

        // Traits flag with no link at all.
        var flagOnly = MutagenFixtures.NewNpc(mod, "FlagButNoLink", traitsTemplate: true);
        Depth(flagOnly.FormKey, Resolver(flagOnly)).Should().Be(0);

        // Cycle: A → B → A.
        var a = MutagenFixtures.NewNpc(mod, "A", traitsTemplate: true);
        var b = MutagenFixtures.NewNpc(mod, "B", traitsTemplate: true, template: a);
        a.Template.SetTo(b.FormKey);
        Depth(a.FormKey, Resolver(a, b)).Should().Be(0);

        // Starting NPC outside the resolver's scope.
        Depth(MutagenFixtures.Fk("000800:Absent.esp"), Resolver()).Should().Be(0);
    }

    [Fact]
    public void Depth_ChainLongerThanMaxDepth_IsZero()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var records = new List<Npc> { MutagenFixtures.NewNpc(mod, "Chain0") };
        for (int i = 1; i <= 6; i++)
        {
            records.Add(MutagenFixtures.NewNpc(mod, "Chain" + i, traitsTemplate: true, template: records[i - 1]));
        }
        var resolve = Resolver(records.ToArray());

        // 6 hops, plus one more visit to confirm the root owns its traits.
        Depth(records[^1].FormKey, resolve, maxDepth: 7).Should().Be(6);
        Depth(records[^1].FormKey, resolve, maxDepth: 6).Should().Be(0);
    }
}
