using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.Models;
using Xunit;
using Xunit.Abstractions;

namespace NPC_Plugin_Chooser_2.Tests.Integration.TemplateMatrix;

/// <summary>
/// The generalised form of the defect fixed in <c>fb177cb</c>, tested directly rather than route by
/// route.
///
/// <para><c>RecordHandler.DuplicateInOrAddFormLink</c> early-returns when the TARGET link's FormKey
/// is already in the duplicate-in mapping: it repoints the link and never traverses the source. In
/// record mode the target is an override of the WINNING record, whose appearance links are usually
/// vanilla or empty, so the early return rarely fires. In SkyPatcher mode the target is a
/// <c>DeepCopyIn</c> of the donor, so every one of its appearance links already EQUALS the donor's —
/// the early return can fire for <c>WornArmor</c>, <c>HeadTexture</c>, <c>HairColor</c> and each
/// <c>HeadPart</c>.</para>
///
/// <para>If that made SkyPatcher merge strictly fewer records, the fix would belong in the early
/// return rather than in each of the seven wig/antler callers. So: give the donor all four of those
/// from a plugin outside the load order, patch in both modes, and diff the merged set. Wig and
/// antler handling are OFF here on purpose — this is about the surrogate, not about wigs.</para>
///
/// <para><b>Two NPCs, sharing every donor record.</b> The mapping is reset per appearance-mod batch,
/// so with a single NPC it is still empty when <c>CopyAppearanceData</c> runs and the early return
/// cannot fire at all. The second NPC is what actually arms it: by then the donor's skin, head
/// texture, hair colour and head part are all mapped, and the second surrogate's links equal exactly
/// those keys. Without it this test would pass vacuously.</para>
///
/// <para>Skips gracefully without a Skyrim SE install.</para>
/// </summary>
[Collection(NpcChooserIntegrationCollection.Name)]
public class SurrogateUnderMergeTests
{
    private readonly ITestOutputHelper _output;

    public SurrogateUnderMergeTests(ITestOutputHelper output) => _output = output;

    private const string EidNpcA = "NPC2Route_SurrogateA";
    private const string EidNpcB = "NPC2Route_SurrogateB";

    [Fact]
    public async Task SkyPatcherSurrogate_MergesTheSameAppearanceRecordsAsRecordMode()
    {
        using var fx = new WigRouteFixture("surrogate");

        var npcA = fx.AddBaseNpc(EidNpcA);
        var npcB = fx.AddBaseNpc(EidNpcB);
        var npcKeyA = npcA.FormKey;
        var npcKeyB = npcB.FormKey;

        // --- everything the donor contributes lives in the resource plugin ---------------------
        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var skin = fx.AddResArmor("NPC2Route_Skin", bodyArma);

        var headTxst = fx.ResMod.TextureSets.AddNew();
        headTxst.EditorID = "NPC2Route_HeadTXST";
        headTxst.Diffuse = "textures/npc2route/head.dds";

        var hairColor = fx.ResMod.Colors.AddNew();
        hairColor.EditorID = "NPC2Route_HairCLFM";
        hairColor.Color = System.Drawing.Color.FromArgb(0x20, 0x30, 0x40);

        // Head part with its own TextureSet and an ExtraPart, so the diff also covers recursion
        // one level below the NPC's direct links.
        var hairLine = fx.ResMod.HeadParts.AddNew();
        hairLine.EditorID = "NPC2Route_HairLine";
        hairLine.Type = HeadPart.TypeEnum.Hair;

        var hpTxst = fx.ResMod.TextureSets.AddNew();
        hpTxst.EditorID = "NPC2Route_HairTXST";
        hpTxst.Diffuse = "textures/npc2route/hair.dds";

        var donorHair = fx.ResMod.HeadParts.AddNew();
        donorHair.EditorID = "NPC2Route_DonorHair";
        donorHair.Type = HeadPart.TypeEnum.Hair;
        donorHair.TextureSet.SetTo(hpTxst);
        donorHair.ExtraParts.Add(hairLine.FormKey.ToLink<IHeadPartGetter>());

        // --- the appearance plugin points BOTH NPCs at all of it ---------------------------------
        // Sharing every record is the point: by the time the second NPC is patched, each of these
        // keys is already in the duplicate-in mapping, which is the only state in which the early
        // return can fire.
        foreach (var baseNpc in new[] { npcA, npcB })
        {
            var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(baseNpc);
            modNpc.WornArmor.SetTo(skin);
            modNpc.HeadTexture.SetTo(headTxst);
            modNpc.HairColor.SetTo(hairColor);
            modNpc.HeadParts.Clear();
            modNpc.HeadParts.Add(donorHair.FormKey);
        }

        fx.WriteFaceGen(npcKeyA);
        fx.WriteFaceGen(npcKeyB);
        fx.WritePlugins();

        // --- run both modes -----------------------------------------------------------------------
        var identities = new Dictionary<bool, HashSet<string>>();
        foreach (var skyPatcherMode in new[] { false, true })
        {
            var label = skyPatcherMode ? "skypatcher" : "record";
            var settings = fx.NewSettings(skyPatcherMode, label);
            var modSetting = fx.NewModSetting();
            settings.ModSettings = new List<ModSetting> { modSetting };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [npcKeyA] = (WigRouteFixture.ModName, npcKeyA),
                [npcKeyB] = (WigRouteFixture.ModName, npcKeyB),
            };

            using var run = await fx.RunAsync(settings, _output, label);
            if (run == null) return; // no Skyrim install

            run.Log.Should().NotContain("FATAL SAVE ERROR", $"[{label}] the plugin must be writable");
            run.PluginExists.Should().BeTrue($"[{label}] the patcher must write an output plugin");

            OutputLinkSweep.DumpRecords(run.Output, _output, label);
            OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
                $"[{label}] the donor's appearance records all live outside the load order");

            // The route's visible effect: BOTH NPCs really did get the donor's appearance, and every
            // link landed on the merged copy rather than on the original. Asserted per NPC because
            // the second one is the one that goes through the early return.
            var outNpcs = run.Output.Npcs.ToList();
            outNpcs.Should().HaveCount(2, $"[{label}] both NPCs are patched");
            foreach (var outNpc in outNpcs)
            {
                var who = $"[{label}] {outNpc.EditorID}";
                outNpc.WornArmor.FormKey.ModKey.Should().Be(run.Output.ModKey, $"{who} WornArmor merged");
                outNpc.HeadTexture.FormKey.ModKey.Should().Be(run.Output.ModKey, $"{who} HeadTexture merged");
                outNpc.HairColor.FormKey.ModKey.Should().Be(run.Output.ModKey, $"{who} HairColor merged");
                outNpc.HeadParts.Should().ContainSingle($"{who} the donor's one head part carried over")
                    .Which.FormKey.ModKey.Should().Be(run.Output.ModKey, $"{who} HeadPart merged");
            }

            // Both NPCs must land on the SAME merged copies — the shared donor record is merged once
            // and every reference redirected to it, in either mode.
            outNpcs.Select(n => n.WornArmor.FormKey).Distinct().Should().ContainSingle(
                $"[{label}] the shared donor skin must merge exactly once");
            outNpcs.Select(n => n.HeadParts.Single().FormKey).Distinct().Should().ContainSingle(
                $"[{label}] the shared donor head part must merge exactly once");

            // Npc excluded: the surrogate is deliberately renamed "<donor>_Template", so the NPC
            // record is the one identity that legitimately differs between modes.
            identities[skyPatcherMode] = run.Output.EnumerateMajorRecords()
                .Where(r => r is not INpcGetter)
                .Select(r => $"{r.Registration.Name} '{r.EditorID ?? "(no EditorID)"}'")
                .ToHashSet();
        }

        _output.WriteLine("record mode merged: " + string.Join(", ", identities[false].OrderBy(s => s)));
        _output.WriteLine("skypatcher merged:  " + string.Join(", ", identities[true].OrderBy(s => s)));

        // Every record the donor supplies must be merged in BOTH modes. Stated as two explicit
        // difference lists so a failure names exactly what diverged, in which direction.
        identities[false].Except(identities[true]).OrderBy(s => s).ToList().Should().BeEmpty(
            "the SkyPatcher surrogate carries the donor's own appearance links, so the " +
            "DuplicateInOrAddFormLink early return must not cost it a merge that record mode gets");

        identities[true].Except(identities[false]).OrderBy(s => s).ToList().Should().BeEmpty(
            "neither mode should merge records the other does not");

        foreach (var expected in new[]
                 {
                     "Armor 'NPC2Route_Skin'",
                     "ArmorAddon 'NPC2Route_BodyAA'",
                     "TextureSet 'NPC2Route_HeadTXST'",
                     "TextureSet 'NPC2Route_HairTXST'",
                     "ColorRecord 'NPC2Route_HairCLFM'",
                     "HeadPart 'NPC2Route_DonorHair'",
                     "HeadPart 'NPC2Route_HairLine'",
                 })
        {
            identities[false].Should().Contain(expected, "record mode must merge the donor's chain");
            identities[true].Should().Contain(expected, "SkyPatcher mode must merge the donor's chain");
        }
    }

    /// <summary>
    /// CHARACTERIZATION, not endorsement. With Include Outfit OFF the two modes do NOT produce the
    /// same output: the SkyPatcher surrogate is a <c>DeepCopyIn</c> of the donor, so it keeps the
    /// donor's <c>DefaultOutfit</c>, and the Patcher's final <c>onlySubRecords</c> merge walker
    /// follows it — dragging the donor's outfit and everything it contains into the output. In
    /// record mode the target keeps the WINNING record's outfit, so none of that is merged.
    ///
    /// <para>It is runtime-inert: <c>ApplySkyPatcherDirectives</c> only emits <c>outfitDefault=</c>
    /// when Include Outfit is on (or a wig outfit was forwarded), so nothing applies the surrogate's
    /// outfit field in game. The cost is output bloat — the outfit's whole item chain, plus the
    /// assets <c>CollectShallowAssetLinks</c> then copies for it.</para>
    ///
    /// <para>This test exists so the divergence is visible and measured rather than latent. If it is
    /// ever fixed (e.g. by clearing the surrogate's DefaultOutfit when Include Outfit is off), this
    /// test should fail and be rewritten to assert the new, matching behaviour.</para>
    /// </summary>
    [Fact]
    public async Task SkyPatcherSurrogate_MergesTheDonorOutfitEvenWithIncludeOutfitOff()
    {
        using var fx = new WigRouteFixture("surrogate-outfit");
        var npc = fx.AddBaseNpc("NPC2Route_OutfitBleed");

        var bodyArma = fx.AddResArmorAddon("NPC2Route_BodyAA", BipedObjectFlag.Body);
        var dress = fx.AddResArmor("NPC2Route_Dress", bodyArma);
        var donorOutfit = fx.AddResOutfit("NPC2Route_DonorOutfit", dress);

        var modNpc = fx.AppearanceMod.Npcs.GetOrAddAsOverride(npc);
        modNpc.DefaultOutfit.SetTo(donorOutfit);

        fx.WriteFaceGen(npc.FormKey);
        fx.WritePlugins();

        var merged = new Dictionary<bool, HashSet<string>>();
        foreach (var skyPatcherMode in new[] { false, true })
        {
            var label = skyPatcherMode ? "skypatcher" : "record";
            var settings = fx.NewSettings(skyPatcherMode, label);
            var modSetting = fx.NewModSetting();
            modSetting.IncludeOutfits = false;
            settings.ModSettings = new List<ModSetting> { modSetting };
            settings.SelectedAppearanceMods = new Dictionary<FormKey, (string, FormKey)>
            {
                [npc.FormKey] = (WigRouteFixture.ModName, npc.FormKey),
            };

            using var run = await fx.RunAsync(settings, _output, label);
            if (run == null) return;

            run.Log.Should().NotContain("FATAL SAVE ERROR");
            OutputLinkSweep.DumpRecords(run.Output, _output, label);
            // Whatever each mode merges, it must at least be self-consistent.
            OutputLinkSweep.AssertNoLinksOutsideLoadOrder(run.Output, run.LoadOrderKeys, _output,
                $"[{label}] the donor outfit chain lives outside the load order");

            merged[skyPatcherMode] = OutputLinkSweep.MergedRecordIdentities(run.Output);
        }

        merged[false].Should().NotContain("Outfit 'NPC2Route_DonorOutfit'",
            "record mode leaves the winning record's outfit alone when Include Outfit is off");

        merged[true].Should().Contain("Outfit 'NPC2Route_DonorOutfit'",
            "the surrogate keeps the donor's DefaultOutfit, and the final merge walker follows it — " +
            "measured behaviour, and the reason SkyPatcher output is larger than record output here");
        merged[true].Should().Contain("Armor 'NPC2Route_Dress'",
            "the bleed is not just the outfit record: its whole item chain comes with it");
    }
}
