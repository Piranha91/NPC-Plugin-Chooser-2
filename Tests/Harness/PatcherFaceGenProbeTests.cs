using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// The Patcher's FaceGen compatibility-probe routing, exercised on the three private statics that
/// decide it (via <see cref="Reflect.InvokeStatic{TOwner,T}"/> — no constructed Patcher, no
/// environment): which decisions probe the borrowed origin/winner mesh
/// (<c>NeedsCompatibilityCheck</c>), which probe the mod's own mesh (<c>ProbesModMesh</c>, the
/// row-2 FaceGen-only shape added when seam 4 closed, 2026-07-30), and which record every probe
/// grades against (<c>ChooseCompatibilityRecord</c>, the flatten-terminus preference that closed
/// seam 3 the same day).
/// </summary>
public class PatcherFaceGenProbeTests
{
    private static readonly FormKey Subject = FormKey.Factory("013BA5:Skyrim.esm");

    private static FaceGenLadderInputs Inputs(
        FaceGenAssetPresence sourceNif,
        FaceGenAssetPresence sourceDds,
        bool hasPluginRecord,
        FaceGenAssetPresence originNif = FaceGenAssetPresence.LooseFile,
        bool winnerNif = true) =>
        new(
            NpcIdentifier: "Probe NPC (013BA5:Skyrim.esm)",
            TargetFormKey: Subject,
            DonorFormKey: Subject,
            SubjectFormKey: Subject,
            ChainStatus: FaceGenChainStatus.NotTemplated,
            ModName: "Some Mod",
            Mode: FaceGenDestinationMode.Record,
            SourceNif: sourceNif,
            SourceDds: sourceDds,
            SourceHasPluginRecord: hasPluginRecord,
            OriginRecordExists: true,
            OriginNif: originNif,
            OriginDds: FaceGenAssetPresence.LooseFile,
            WinnerNifExists: winnerNif,
            WinnerNifOwner: "Other Mod",
            WinnerDdsExists: true,
            OriginNifCompatible: null,
            WinnerNifCompatible: null,
            LegacyDonorNif: sourceNif,
            LegacyDonorDds: sourceDds);

    private static bool NeedsCheck(FaceGenLadderDecision d) =>
        Reflect.InvokeStatic<Patcher, bool>("NeedsCompatibilityCheck", d);

    private static bool ProbesMod(FaceGenLadderDecision d) =>
        Reflect.InvokeStatic<Patcher, bool>("ProbesModMesh", d);

    [Fact]
    public void RowTwoFaceGenOnly_ProbesTheModMesh_AndNotTheFallbacks()
    {
        // Mod ships the mesh, no tint, no record: the mesh pairs with the origin's record.
        var d = FaceGenLadder.Classify(Inputs(
            FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.NotFound, hasPluginRecord: false));

        d.Row.Should().Be(FaceGenLadderRow.NifOnly);
        ProbesMod(d).Should().BeTrue();
        NeedsCheck(d).Should().BeFalse(
            "the mod's own mesh ships, so the origin/winner fallbacks are not consulted for it");
    }

    [Fact]
    public void RowTwoWithARecord_ProbesNothing()
    {
        // Mesh and record share an author — self-consistent, like row 1.
        var d = FaceGenLadder.Classify(Inputs(
            FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.NotFound, hasPluginRecord: true));

        d.Row.Should().Be(FaceGenLadderRow.NifOnly);
        ProbesMod(d).Should().BeFalse();
        NeedsCheck(d).Should().BeFalse();
    }

    [Fact]
    public void RowOne_ProbesNothing_EvenWhenFaceGenOnly()
    {
        var d = FaceGenLadder.Classify(Inputs(
            FaceGenAssetPresence.LooseFile, FaceGenAssetPresence.LooseFile, hasPluginRecord: false));

        d.Row.Should().Be(FaceGenLadderRow.NifAndDds);
        ProbesMod(d).Should().BeFalse();
        NeedsCheck(d).Should().BeFalse();
    }

    [Fact]
    public void RowThree_ProbesTheFallbacks_AndNotTheModMesh()
    {
        var d = FaceGenLadder.Classify(Inputs(
            FaceGenAssetPresence.NotFound, FaceGenAssetPresence.LooseFile, hasPluginRecord: true));

        d.Row.Should().Be(FaceGenLadderRow.DdsOnlyWithRecord);
        NeedsCheck(d).Should().BeTrue();
        ProbesMod(d).Should().BeFalse("there is no mod mesh to probe");
    }

    [Fact]
    public void AnAbort_ProbesNothing()
    {
        // Row 3 with no origin mesh and no winner: aborts, and neither probe fires on a corpse.
        var d = FaceGenLadder.Classify(Inputs(
            FaceGenAssetPresence.NotFound, FaceGenAssetPresence.LooseFile, hasPluginRecord: true,
            originNif: FaceGenAssetPresence.NotFound, winnerNif: false));

        d.Abort.Should().BeTrue();
        NeedsCheck(d).Should().BeFalse();
        ProbesMod(d).Should().BeFalse();
    }

    [Fact]
    public void DescribeMismatch_NamesRecordSideParts_MeshSideShapes_AndTheWinningOverride()
    {
        // The probe's evidence for the detailed warning log: which head parts the record
        // resolves (tagging the plugin whose override currently WINS the part when that is not
        // the definer — the "which plugin is overwriting" answer), and which shapes the mesh
        // bakes that match nothing.
        var analysis = new FaceGenConsistencyAnalyzer.Result
        {
            NifParsed = true,
            MissingBakedShapes =
            [
                new FaceGenConsistencyAnalyzer.HeadPartRef(
                    FormKey.Factory("000011:RSkyrimChildren.esm"), "0RCOChildHeadNord"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(
                    FormKey.Factory("017508:Skyrim.esm"), "HumanBeard06"),
            ],
            OrphanBakedShapes = ["ChildEyes", "ChildMouth"],
        };

        string text = Reflect.InvokeStatic<Patcher, string>("DescribeMismatch", analysis,
            (Func<FormKey, string?>)(fk => fk.ModKey.FileName == "Skyrim.esm" ? "KLHairdos.esp" : null))!;

        text.Should().Contain("'0RCOChildHeadNord' (000011:RSkyrimChildren.esm)")
            .And.NotContain("000011:RSkyrimChildren.esm, winning override",
                "a part supplied by its own definer needs no override tag")
            .And.Contain("'HumanBeard06' (017508:Skyrim.esm, winning override: KLHairdos.esp)")
            .And.Contain("mesh bakes (no matching head part): ChildEyes, ChildMouth");
    }

    [Fact]
    public void ChooseCompatibilityRecord_PrefersTerminus_ThenOrigin_ThenDonor()
    {
        // The precedence IS the seam-3 fix: a flattened chain overlays the terminus's head parts
        // onto whatever ships, so the probe must grade against the terminus, not the donor.
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Probe.esp"), SkyrimRelease.SkyrimSE);
        var terminus = mod.Npcs.AddNew();
        var origin = mod.Npcs.AddNew();
        var donor = mod.Npcs.AddNew();

        Choose(terminus, origin, donor).Should().BeSameAs(terminus);
        Choose(null, origin, donor).Should().BeSameAs(origin);
        Choose(null, null, donor).Should().BeSameAs(donor);
        return;

        INpcGetter Choose(INpcGetter? t, INpcGetter? o, INpcGetter d) =>
            Reflect.InvokeStatic<Patcher, INpcGetter>("ChooseCompatibilityRecord", t, o, d)!;
    }
}
