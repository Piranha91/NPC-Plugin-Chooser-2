using System.Collections.Generic;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.Integration.GoldenOutput;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration;

/// <summary>
/// Guards the 2.2.3 appearance-race rule (<see cref="Auxilliary.IsValidAppearanceRace"/>) against
/// real vanilla records, which is the only place its behaviour can be honestly asserted: every
/// interesting case is a quirk of Bethesda's own data.
///
/// <para>The rule reads the RACE record's <c>FaceGenHead</c> flag rather than the
/// <c>ActorTypeNPC</c> keyword, and reads it from the template chain TERMINUS rather than from the
/// NPC's own record. Both halves were derived from a 183-plugin census and confirmed in game by
/// spawning specimens; the specimens below are the vanilla subset of that evidence.</para>
///
/// <para>Skips gracefully without a local Skyrim SE install (same fixture as the golden suite).</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class AppearanceRaceRuleTests : IClassFixture<GoldenEnvFixture>
{
    private readonly GoldenEnvFixture _env;
    private readonly ITestOutputHelper _output;

    public AppearanceRaceRuleTests(GoldenEnvFixture env, ITestOutputHelper output)
    {
        _env = env;
        _output = output;
    }

    private bool Skip()
    {
        if (_env.Available) return false;
        _output.WriteLine("SKIPPED: " + _env.SkipReason);
        return true;
    }

    // ---- Specimens -------------------------------------------------------------------------
    // Every one of these is a real vanilla record. The comment on each is what the rule must say
    // about it, and why.

    /// <summary>
    /// Miraak. DLC2MiraakRace carries FaceGenHead but NOT ActorTypeNPC, so the old keyword rule
    /// discarded him from every mod that touched him — the bug that prompted the rewrite.
    /// </summary>
    private static readonly FormKey Miraak = FormKey.Factory("01FB98:Dragonborn.esm");

    /// <summary>
    /// Movarth's vampire boss record — Traits-templated to a real NPC, so its appearance comes
    /// from the chain terminus. (Its race field is the DefaultRace placeholder in Skyrim.esm, but
    /// USSEP corrects that, so the test asserts only the verdict, not the field.)
    /// </summary>
    private static readonly FormKey MovarthVampireBoss = FormKey.Factory("08BB91:Skyrim.esm");

    /// <summary>Ordinary humanoid control — NordRace, untemplated, unambiguously has a face.</summary>
    private static readonly FormKey Balgruuf = FormKey.Factory("013BBF:Skyrim.esm");

    /// <summary>
    /// Pumpkin, the Southfringe pet fox. FoxRace, Traits-templated to another fox. The old rule's
    /// FoxRace special case (added because Bethesda templated some HUMANS onto FoxRace) let actual
    /// foxes into the list; resolving the terminus removes the need for that exception entirely.
    /// </summary>
    private static readonly FormKey PetFox = FormKey.Factory("0B11A7:Skyrim.esm");

    /// <summary>
    /// An audio-template dummy: DefaultRace, untemplated, and it SHIPS FaceGen in the vanilla BSA
    /// despite having no head parts and no face. Proof that shipped FaceGen cannot be used as
    /// evidence of a face, and the reason DefaultRace is excluded outright.
    /// </summary>
    private static readonly FormKey AudioTemplateElk = FormKey.Factory("0FFA08:Skyrim.esm");

    /// <summary>Creature control — a wolf has neither signal and must stay out.</summary>
    private static readonly FormKey EncWolf = FormKey.Factory("023A91:Skyrim.esm");

    public static IEnumerable<object[]> Specimens => new List<object[]>
    {
        new object[] { Miraak,              true,  "Miraak (FaceGenHead without ActorTypeNPC)" },
        new object[] { MovarthVampireBoss,  true,  "a Traits-templated named NPC resolving to a humanoid" },
        new object[] { Balgruuf,            true,  "ordinary NordRace humanoid" },
        new object[] { PetFox,              false, "an actual fox templated to an actual fox" },
        new object[] { AudioTemplateElk,    false, "DefaultRace placeholder dummy that ships FaceGen" },
        new object[] { EncWolf,             false, "creature race" },
    };

    [Theory]
    [MemberData(nameof(Specimens))]
    public void Rule_MatchesInGameVerifiedVerdict(FormKey npcFormKey, bool expectedValid, string why)
    {
        if (Skip()) return;

        var provider = _env.Env!.Provider;
        var aux = new Auxilliary(provider);

        provider.LinkCache.TryResolve<INpcGetter>(npcFormKey, out var npc).Should().BeTrue(
            $"{npcFormKey} ({why}) must exist in the test load order");

        INpcGetter? Resolve(FormKey fk) =>
            provider.LinkCache.TryResolve<INpcGetter>(fk, out var g) ? g : null;

        var isValid = aux.IsValidAppearanceRace(npc!.Race.FormKey, npc, null,
            out var rejectionMessage, out _, resolveNpc: Resolve);

        _output.WriteLine($"{npcFormKey} [{npc.EditorID}] -> valid={isValid} {rejectionMessage}");
        isValid.Should().Be(expectedValid, $"{npc.EditorID} is {why}");
    }

    /// <summary>
    /// The terminus walk is the half of the rule that is easy to regress silently: unwire the
    /// resolver and every call still compiles and still returns a verdict — just the wrong one,
    /// read off an inert race field. So assert that at least one real NPC exists whose verdict
    /// DEPENDS on the walk, and that the walk is what makes it valid.
    ///
    /// <para>The specimen is discovered rather than hard-coded: which NPCs carry a junk race field
    /// is load-order specific (USSEP repairs several of Bethesda's), but that the category exists
    /// at all is not — it is why the old rule needed a FoxRace special case.</para>
    /// </summary>
    [Fact]
    public void TerminusResolution_ChangesTheVerdictForRecordsWithAnInertRaceField()
    {
        if (Skip()) return;

        var provider = _env.Env!.Provider;
        var aux = new Auxilliary(provider);

        INpcGetter? Resolve(FormKey fk) =>
            provider.LinkCache.TryResolve<INpcGetter>(fk, out var g) ? g : null;

        INpcGetter? specimen = null;
        string ownRaceRejection = string.Empty;

        foreach (var npc in provider.LoadOrder.PriorityOrder.Npc().WinningOverrides())
        {
            if (!Auxilliary.IsValidTemplatedNpc(npc)) continue;

            // Judged on its own (inert) race field.
            if (aux.IsValidAppearanceRace(npc.Race.FormKey, npc, null, out var ownMessage, out _)) continue;

            // Judged on the record that actually renders.
            if (!aux.IsValidAppearanceRace(npc.Race.FormKey, npc, null, out _, out _, resolveNpc: Resolve)) continue;

            specimen = npc;
            ownRaceRejection = ownMessage;
            break;
        }

        specimen.Should().NotBeNull(
            "vanilla contains templated NPCs whose own race field is junk (FoxRace/DefaultRace) while " +
            "their chain terminus is a real humanoid — the whole reason the gate moved to the terminus");

        _output.WriteLine(
            $"{specimen!.FormKey} [{specimen.EditorID}] own race {specimen.Race.FormKey} rejected as: " +
            $"{ownRaceRejection} — rescued by resolving its template chain.");
    }
}
